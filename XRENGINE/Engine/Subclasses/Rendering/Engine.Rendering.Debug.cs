using XREngine.Extensions;
using JoltPhysicsSharp;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Debugging;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.UI;
using XREngine.Scene;
using Triangle = XREngine.Data.Geometry.Triangle;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Debug rendering functions.
            /// These should not be used in production code.
            /// </summary>
            public static class Debug
            {
                private static long _debugDrawComponentCallbacks;
                public static long LastDebugDrawComponentCallbackCount { get; private set; }

                public static void RecordDebugDrawComponentCallback()
                    => Interlocked.Increment(ref _debugDrawComponentCallbacks);

                static Debug()
                {
                    Engine.Time.Timer.PreUpdateFrame += PreUpdate;
                    Engine.Time.Timer.SwapBuffers += SwapBuffers;
                }

                private static void PreUpdate()
                {
                }

                private static void ClearQueues()
                {
                    _debug3D.ClearQueues();
                    _debug2D.ClearQueues();
                }

                public static readonly Vector3 UIPositionBias = new(0.0f, 0.0f, 0.1f);
                public static readonly Rotator UIRotation = new(90.0f, 0.0f, 0.0f, ERotationOrder.YPR);

                private enum EDebugPrimitiveSceneKind
                {
                    Scene3D,
                    Scene2D,
                }

                /// <summary>
                /// Owns one debug-line draw batch for a specific world-space line width.
                /// Keeping widths in separate batches lets BVH overlays preserve their per-rig
                /// settings without changing the generic debug-line buffer layout.
                /// </summary>
                private sealed class DebugLineOverlayBatch(float lineWidth)
                {
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, ColorF4 color)> Lines = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, ColorF4 color)> LineQueue = [];
                    public readonly InstancedDebugVisualizer Visualizer = new(0.005f, lineWidth);

                    public void AddLine(Vector3 start, Vector3 end, ColorF4 color, bool isRenderThread)
                    {
                        if (isRenderThread)
                            Lines.Add((start, end, color));
                        else
                            LineQueue.Enqueue((start, end, color));
                    }

                    public void DequeueLines()
                    {
                        while (LineQueue.TryDequeue(out var line))
                            Lines.Add(line);
                    }

                    public void Populate()
                    {
                        uint lineCount = (uint)Lines.Count;
                        Visualizer.LineCount = lineCount;

                        int index = 0;
                        foreach (var (start, end, color) in Lines)
                        {
                            if ((uint)index >= lineCount)
                                break;
                            Visualizer.SetLineAt(index++, start, end, color);
                        }

                        Lines.Clear();
                    }

                    public void Render()
                    {
                        if (Visualizer.LineCount > 0)
                            Visualizer.Render();
                    }

                    public void ClearQueues()
                        => LineQueue.Clear();

                    public void ClearBags()
                        => Lines.Clear();

                    public void ClearVisuals()
                        => Visualizer.Clear();
                }

                /// <summary>
                /// Groups CPU-authored overlay lines by configured width. Widths are quantized
                /// to the same 0.0001 precision exposed by the editor controls so dragging a
                /// setting cannot create an unbounded number of persistent renderer batches.
                /// </summary>
                private sealed class DebugLineOverlayState
                {
                    private const float LineWidthStep = 0.0001f;
                    private const float MinimumLineWidth = 0.0001f;
                    private const float MaximumLineWidth = 0.05f;
                    private readonly ConcurrentDictionary<int, DebugLineOverlayBatch> _batches = [];

                    public void AddBox(ReadOnlySpan<Vector3> points, ColorF4 color, float lineWidth)
                    {
                        DebugLineOverlayBatch batch = GetBatch(lineWidth);
                        bool isRenderThread = Engine.IsRenderThread;
                        batch.AddLine(points[0], points[1], color, isRenderThread);
                        batch.AddLine(points[1], points[2], color, isRenderThread);
                        batch.AddLine(points[2], points[3], color, isRenderThread);
                        batch.AddLine(points[3], points[0], color, isRenderThread);
                        batch.AddLine(points[4], points[5], color, isRenderThread);
                        batch.AddLine(points[5], points[6], color, isRenderThread);
                        batch.AddLine(points[6], points[7], color, isRenderThread);
                        batch.AddLine(points[7], points[4], color, isRenderThread);
                        batch.AddLine(points[0], points[4], color, isRenderThread);
                        batch.AddLine(points[1], points[5], color, isRenderThread);
                        batch.AddLine(points[2], points[6], color, isRenderThread);
                        batch.AddLine(points[3], points[7], color, isRenderThread);
                    }

                    public void DequeueLines()
                    {
                        foreach (var pair in _batches)
                            pair.Value.DequeueLines();
                    }

                    public void Populate()
                    {
                        foreach (var pair in _batches)
                            pair.Value.Populate();
                    }

                    public void Render()
                    {
                        foreach (var pair in _batches)
                            pair.Value.Render();
                    }

                    public void ClearQueues()
                    {
                        foreach (var pair in _batches)
                            pair.Value.ClearQueues();
                    }

                    public void ClearBags()
                    {
                        foreach (var pair in _batches)
                            pair.Value.ClearBags();
                    }

                    public void ClearVisuals()
                    {
                        foreach (var pair in _batches)
                            pair.Value.ClearVisuals();
                    }

                    private DebugLineOverlayBatch GetBatch(float lineWidth)
                    {
                        float normalizedWidth = Math.Clamp(lineWidth, MinimumLineWidth, MaximumLineWidth);
                        normalizedWidth = MathF.Round(normalizedWidth / LineWidthStep) * LineWidthStep;
                        int key = BitConverter.SingleToInt32Bits(normalizedWidth);
                        return _batches.GetOrAdd(
                            key,
                            static (_, width) => new DebugLineOverlayBatch(width),
                            normalizedWidth);
                    }
                }

                private sealed class DebugPrimitiveSceneState
                {
                    public readonly ConcurrentBag<(Vector3 pos, ColorF4 color)> Points = [];
                    public readonly ConcurrentBag<(Vector3 pos, ColorF4 color)> DepthTestedPoints = [];
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, ColorF4 color)> Lines = [];
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, ColorF4 color)> DepthTestedLines = [];
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> Triangles = [];
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> DepthTestedTriangles = [];

                    public readonly ConcurrentQueue<(Vector3 pos, ColorF4 color)> PointQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos, ColorF4 color)> DepthTestedPointQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, ColorF4 color)> LineQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, ColorF4 color)> DepthTestedLineQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> TriangleQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> DepthTestedTriangleQueue = [];
                    public readonly ConcurrentQueue<DebugShapeInstance> ShapeInstanceQueue = [];

                    public readonly ConcurrentDictionary<int, (UIText text, float lastUpdatedTime)> Texts = new();
                    public readonly ConcurrentQueue<(Vector3 pos, string text, ColorF4 color, float scale)> TextUpdateQueue = [];

                    public readonly InstancedDebugVisualizer Visualizer = new();
                    public readonly InstancedDebugVisualizer DepthTestedVisualizer = new(depthTested: true);
                    public readonly DebugLineOverlayState BaseOverlayLines = new();
                    public readonly DebugLineOverlayState HighlightOverlayLines = new();

                    public void ClearQueues()
                    {
                        TextUpdateQueue.Clear();
                        PointQueue.Clear();
                        DepthTestedPointQueue.Clear();
                        LineQueue.Clear();
                        DepthTestedLineQueue.Clear();
                        TriangleQueue.Clear();
                        DepthTestedTriangleQueue.Clear();
                        ShapeInstanceQueue.Clear();
                        BaseOverlayLines.ClearQueues();
                        HighlightOverlayLines.ClearQueues();
                    }

                    public void ClearBags()
                    {
                        Points.Clear();
                        DepthTestedPoints.Clear();
                        Lines.Clear();
                        DepthTestedLines.Clear();
                        Triangles.Clear();
                        DepthTestedTriangles.Clear();
                        BaseOverlayLines.ClearBags();
                        HighlightOverlayLines.ClearBags();
                    }

                    public void ClearVisuals()
                    {
                        Visualizer.Clear();
                        DepthTestedVisualizer.Clear();
                        BaseOverlayLines.ClearVisuals();
                        HighlightOverlayLines.ClearVisuals();
                    }
                }

                private static readonly DebugPrimitiveSceneState _debug3D = new();
                private static readonly DebugPrimitiveSceneState _debug2D = new();
                [ThreadStatic]
                private static DebugPrimitiveSceneState? _expandingShapeScene;
                [ThreadStatic]
                private static bool _expandingShapeDepthTested;

                public static void SwapBuffers()
                {
                    using var sample = Engine.Profiler.Start("Rendering.Debug.SwapBuffers");
                    LastDebugDrawComponentCallbackCount =
                        Interlocked.Exchange(ref _debugDrawComponentCallbacks, 0);

                    if (Engine.ShuttingDown)
                    {
                        ClearQueues();
                        ClearDebugShapeBags();
                        ClearDebugVisualizers();
                        return;
                    }

                    if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                    {
                        ClearDebugShapeQueues();
                        ClearDebugShapeBags();
                        ClearDebugVisualizers();
                        return;
                    }

                    DequeueDebugShapeItems();
                    ApplyDebugPrimitivePreferences();

                    var mode = Engine.EditorPreferences?.Debug?.DebugShapePopulationMode
                        ?? EDebugShapePopulationMode.Tasks;

                    try
                    {
                        switch (mode)
                        {
                            case EDebugShapePopulationMode.Tasks:
                                PopulateDebugScene(_debug3D);
                                PopulateDebugScene(_debug2D);
                                break;
                            case EDebugShapePopulationMode.ParallelInvoke:
                                PopulateDebugScene(_debug3D);
                                PopulateDebugScene(_debug2D);
                                break;
                            case EDebugShapePopulationMode.Sequential:
                            default:
                                PopulateDebugScene(_debug3D);
                                PopulateDebugScene(_debug2D);
                                break;
                        }

                        PopulateOverlayLines(_debug3D);
                        PopulateOverlayLines(_debug2D);
                    }
                    catch (OperationCanceledException) when (Engine.ShuttingDown)
                    {
                        ClearQueues();
                    }
                    catch (AggregateException ex) when (Engine.ShuttingDown && IsCancellationOnly(ex))
                    {
                        ClearQueues();
                    }
                }

                private static void ClearDebugShapeQueues()
                {
                    _debug3D.ClearQueues();
                    _debug2D.ClearQueues();
                }

                private static void ClearDebugShapeBags()
                {
                    _debug3D.ClearBags();
                    _debug2D.ClearBags();
                }

                private static void ClearDebugVisualizers()
                {
                    _debug3D.ClearVisuals();
                    _debug2D.ClearVisuals();
                }

                private static bool IsCancellationOnly(AggregateException exception)
                {
                    foreach (Exception inner in exception.Flatten().InnerExceptions)
                    {
                        if (inner is not OperationCanceledException)
                            return false;
                    }

                    return true;
                }

                private static void ApplyDebugPrimitivePreferences()
                {
                    EditorDebugOptions? debug = Engine.EditorPreferences?.Debug;
                    if (debug is null)
                        return;

                    _debug3D.Visualizer.PointSize = debug.DebugPointSize;
                    _debug3D.Visualizer.LineWidth = debug.DebugLineWidth;
                    _debug3D.DepthTestedVisualizer.PointSize = debug.DebugPointSize;
                    _debug3D.DepthTestedVisualizer.LineWidth = debug.DebugLineWidth;
                    _debug2D.Visualizer.PointSize = debug.DebugPointSize;
                    _debug2D.Visualizer.LineWidth = debug.DebugLineWidth;
                    _debug2D.DepthTestedVisualizer.PointSize = debug.DebugPointSize;
                    _debug2D.DepthTestedVisualizer.LineWidth = debug.DebugLineWidth;
                }

                private static void PopulateDebugScene(DebugPrimitiveSceneState scene)
                {
                    PopulatePoints(scene);
                    PopulateLines(scene);
                    PopulateTriangles(scene);
                }

                private static void PopulateOverlayLines(DebugPrimitiveSceneState scene)
                {
                    scene.BaseOverlayLines.Populate();
                    scene.HighlightOverlayLines.Populate();
                }

                private static void PopulateTriangles(DebugPrimitiveSceneState scene)
                {
                    scene.Visualizer.TriangleCount = (uint)scene.Triangles.Count;
                    scene.DepthTestedVisualizer.TriangleCount = (uint)scene.DepthTestedTriangles.Count;

                    int i = 0;
                    foreach (var (pos0, pos1, pos2, color) in scene.Triangles)
                        scene.Visualizer.SetTriangleAt(i++, pos0, pos1, pos2, color);

                    i = 0;
                    foreach (var (pos0, pos1, pos2, color) in scene.DepthTestedTriangles)
                        scene.DepthTestedVisualizer.SetTriangleAt(i++, pos0, pos1, pos2, color);

                    scene.Triangles.Clear();
                    scene.DepthTestedTriangles.Clear();
                }

                private static void PopulateLines(DebugPrimitiveSceneState scene)
                {
                    scene.Visualizer.LineCount = (uint)scene.Lines.Count;
                    scene.DepthTestedVisualizer.LineCount = (uint)scene.DepthTestedLines.Count;

                    int i = 0;
                    foreach (var (pos0, pos1, color) in scene.Lines)
                        scene.Visualizer.SetLineAt(i++, pos0, pos1, color);

                    i = 0;
                    foreach (var (pos0, pos1, color) in scene.DepthTestedLines)
                        scene.DepthTestedVisualizer.SetLineAt(i++, pos0, pos1, color);

                    scene.Lines.Clear();
                    scene.DepthTestedLines.Clear();
                }

                private static void PopulatePoints(DebugPrimitiveSceneState scene)
                {
                    scene.Visualizer.PointCount = (uint)scene.Points.Count;
                    scene.DepthTestedVisualizer.PointCount = (uint)scene.DepthTestedPoints.Count;

                    int i = 0;
                    foreach (var (pos, color) in scene.Points)
                        scene.Visualizer.SetPointAt(i++, pos, color);

                    i = 0;
                    foreach (var (pos, color) in scene.DepthTestedPoints)
                        scene.DepthTestedVisualizer.SetPointAt(i++, pos, color);

                    scene.Points.Clear();
                    scene.DepthTestedPoints.Clear();
                }

                private static XRMeshRenderer? _lineRenderer = null;
                public static XRMeshRenderer GetLineRenderer()
                {
                    if (_lineRenderer is null)
                    {
                        XRMesh mesh = XRMesh.Create(VertexQuad.PosY());
                        XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.White);
                        mat.RenderOptions.DepthTest.Enabled = XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled;
                        mat.RenderOptions.CullMode = ECullMode.None;
                        mat.EnableTransparency();
                        _lineRenderer = new XRMeshRenderer(mesh, mat);
                    }
                    return _lineRenderer;
                }

                private static XRMeshRenderer? _pointRenderer = null;
                public static XRMeshRenderer GetPointRenderer()
                {
                    if (_pointRenderer is null)
                    {
                        XRMesh mesh = XRMesh.Create(VertexQuad.PosZ());
                        XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.White);
                        mat.RenderOptions.DepthTest.Enabled = XREngine.Rendering.Models.Materials.ERenderParamUsage.Disabled;
                        mat.EnableTransparency();
                        _pointRenderer = new XRMeshRenderer(mesh, mat);
                    }
                    return _pointRenderer;
                }

                private static readonly Lock _debugShapeQueueLock = new();
                public static void RenderShapes(bool depthTested)
                {
                    if (!Engine.Rendering.State.IsMainPass)
                        return;

                    if (Engine.Rendering.State.RenderingCamera is { } camera &&
                        !camera.CullingMask.Contains(XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (!depthTested)
                        DequeueDebugTextItems(scene);

                    if (!depthTested && !scene.Texts.IsEmpty)
                    {
                        var hashes = scene.Texts.Keys.ToArray();
                        for (int i = 0; i < hashes.Length; i++)
                        {
                            var hash = hashes[i];
                            if (!scene.Texts.TryGetValue(hash, out var text))
                                continue;

                            text.text.Render();

                            float nowTime = Engine.Time.Timer.Time();
                            float lastTime = text.lastUpdatedTime;
                            if (nowTime - lastTime > Engine.EditorPreferences.Debug.DebugTextMaxLifespan && scene.Texts.TryRemove(hash, out (UIText text, float lastUpdatedTime) item))
                                TextPool.Release(item.text);
                        }
                    }

                    if (Engine.Rendering.State.DebugInstanceRenderingAvailable)
                    {
                        if (depthTested)
                        {
                            scene.DepthTestedVisualizer.Render();
                        }
                        else
                        {
                            scene.BaseOverlayLines.Render();
                            scene.Visualizer.Render();
                            scene.HighlightOverlayLines.Render();
                        }
                    }
                }

                private static DebugPrimitiveSceneState ResolveDebugPrimitiveSceneState()
                    => _expandingShapeScene ??
                    (ResolveDebugPrimitiveSceneKind() == EDebugPrimitiveSceneKind.Scene2D
                        ? _debug2D
                        : _debug3D);

                private static EDebugPrimitiveSceneKind ResolveDebugPrimitiveSceneKind()
                    => Engine.IsRenderThread &&
                        Engine.Rendering.State.RenderingScene is VisualScene2D
                        ? EDebugPrimitiveSceneKind.Scene2D
                        : EDebugPrimitiveSceneKind.Scene3D;

                private static void DequeueDebugTextItems(DebugPrimitiveSceneState scene)
                {
                    while (scene.TextUpdateQueue.TryDequeue(out (Vector3 pos, string text, ColorF4 color, float scale) item))
                        UpdateDebugText(scene, item.pos, item.text, item.color, item.scale);
                }

                private static void DequeueDebugShapeItems()
                {
                    DequeueDebugShapeItems(_debug3D);
                    DequeueDebugShapeItems(_debug2D);
                }

                private static void DequeueDebugShapeItems(DebugPrimitiveSceneState scene)
                {
                    while (scene.PointQueue.TryDequeue(out var point))
                        scene.Points.Add(point);
                    while (scene.DepthTestedPointQueue.TryDequeue(out var point))
                        scene.DepthTestedPoints.Add(point);
                    while (scene.LineQueue.TryDequeue(out var line))
                        scene.Lines.Add(line);
                    while (scene.DepthTestedLineQueue.TryDequeue(out var line))
                        scene.DepthTestedLines.Add(line);
                    while (scene.TriangleQueue.TryDequeue(out var triangle))
                        scene.Triangles.Add(triangle);
                    while (scene.DepthTestedTriangleQueue.TryDequeue(out var triangle))
                        scene.DepthTestedTriangles.Add(triangle);
                    ExpandShapeInstances(scene);
                    scene.BaseOverlayLines.DequeueLines();
                    scene.HighlightOverlayLines.DequeueLines();
                }

                private static void ExpandShapeInstances(DebugPrimitiveSceneState scene)
                {
                    _expandingShapeScene = scene;
                    try
                    {
                        while (scene.ShapeInstanceQueue.TryDequeue(out DebugShapeInstance shape))
                        {
                            _expandingShapeDepthTested = shape.DepthTested;
                            switch (shape.Kind)
                            {
                                case EDebugShapeInstanceKind.Circle:
                                    RenderCircleImmediate(
                                        shape.Position,
                                        Quaternion.CreateFromRotationMatrix(shape.Transform),
                                        shape.Radius,
                                        shape.Solid,
                                        shape.Color);
                                    break;
                                case EDebugShapeInstanceKind.Quad:
                                    RenderQuadImmediate(shape.Position, shape.Rotation, shape.Extents, shape.Solid, shape.Color);
                                    break;
                                case EDebugShapeInstanceKind.Sphere:
                                    RenderSphereImmediate(shape.Position, shape.Radius, shape.Solid, shape.Color);
                                    break;
                                case EDebugShapeInstanceKind.Capsule:
                                    RenderCapsuleImmediate(shape.Position, shape.Axis, shape.Radius, shape.Height, shape.Solid, shape.Color);
                                    break;
                                case EDebugShapeInstanceKind.Cylinder:
                                    RenderCylinderImmediate(shape.Transform, shape.Axis, shape.Radius, shape.Height, shape.Solid, shape.Color);
                                    break;
                                case EDebugShapeInstanceKind.Cone:
                                    RenderConeImmediate(shape.Position, shape.Axis, shape.Radius, shape.Height, shape.Solid, shape.Color);
                                    break;
                            }
                        }
                    }
                    finally
                    {
                        _expandingShapeScene = null;
                        _expandingShapeDepthTested = false;
                    }
                }

                private static void SubmitShape(in DebugShapeInstance shape)
                {
                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    scene.ShapeInstanceQueue.Enqueue(shape);
                }

                private static Matrix4x4 CalculateLineMatrix(Vector3 pos0, Vector3 pos1, float lineWidth, Vector3 camForward, Vector3 camUp, Vector3 camRight)
                {
                    Vector3 dir = pos1 - pos0;
                    Vector3 center = (pos0 + pos1) * 0.5f;
                    float length = dir.Length();
                    float scaleXY = lineWidth;
                    float scaleZ = length;
                    Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(scaleXY, scaleXY, scaleZ);
                    Vector3 lineUp = Vector3.Cross(dir, camRight).Normalized();
                    Matrix4x4 rotMatrix = Matrix4x4.CreateWorld(center, dir, lineUp);
                    return scaleMatrix * rotMatrix;
                }

                private static bool InCamera(Vector3 position)
                {
                    return true;

                    //var playerCam = Engine.State.MainPlayer.ControlledPawn?.GetCamera()?.Camera;
                    //if (playerCam is null)
                    //    return false;

                    ////Transform to clip space
                    //Vector3 vpPos = playerCam.WorldToNormalizedViewportCoordinate(position);

                    //return
                    //    vpPos.X >= 0 &&
                    //    vpPos.X <= 1 &&
                    //    vpPos.Y >= 0 &&
                    //    vpPos.Y <= 1 &&
                    //    vpPos.Z >= playerCam.NearZ &&
                    //    vpPos.Z < playerCam.FarZ;
                }

                public static void RenderPoint(
                    Vector3 position,
                    ColorF4 color,
                    bool depthTested = false)
                {
                    depthTested |= _expandingShapeDepthTested;
                    if (!InCamera(position))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (IsRenderThread)
                    {
                        if (depthTested)
                            scene.DepthTestedPoints.Add((position, color));
                        else
                            scene.Points.Add((position, color));
                    }
                    else
                    {
                        using (_debugShapeQueueLock.EnterScope())
                        {
                            if (depthTested)
                                scene.DepthTestedPointQueue.Enqueue((position, color));
                            else
                                scene.PointQueue.Enqueue((position, color));
                        }
                    }
                }

                public static void RenderRay(Vector3 position, Vector3 direction, ColorF4 color)
                    => RenderLine(position, position + direction, color);

                public static unsafe void RenderLine(
                    Vector3 start,
                    Vector3 end,
                    ColorF4 color,
                    bool depthTested = false)
                {
                    depthTested |= _expandingShapeDepthTested;
                    if (!InCamera(start) && !InCamera(end))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (IsRenderThread)
                    {
                        if (depthTested)
                            scene.DepthTestedLines.Add((start, end, color));
                        else
                            scene.Lines.Add((start, end, color));
                    }
                    else
                    {
                        using (_debugShapeQueueLock.EnterScope())
                        {
                            if (depthTested)
                                scene.DepthTestedLineQueue.Enqueue((start, end, color));
                            else
                                scene.LineQueue.Enqueue((start, end, color));
                        }
                    }
                }

                public static void RenderTriangle(Triangle triangle, ColorF4 color, bool solid, bool depthTested = false)
                    => RenderTriangle(triangle.A, triangle.B, triangle.C, color, solid, depthTested);
                public static void RenderTriangle(
                    Vector3 A,
                    Vector3 B,
                    Vector3 C,
                    ColorF4 color,
                    bool solid,
                    bool depthTested = false)
                {
                    depthTested |= _expandingShapeDepthTested;
                    if (!(InCamera(A) || InCamera(B) || InCamera(C)))
                        return;

                    if (solid)
                    {
                        DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                        if (IsRenderThread)
                        {
                            if (depthTested)
                                scene.DepthTestedTriangles.Add((A, B, C, color));
                            else
                                scene.Triangles.Add((A, B, C, color));
                        }
                        else
                        {
                            using (_debugShapeQueueLock.EnterScope())
                            {
                                if (depthTested)
                                    scene.DepthTestedTriangleQueue.Enqueue((A, B, C, color));
                                else
                                    scene.TriangleQueue.Enqueue((A, B, C, color));
                            }
                        }
                    }
                    else
                    {
                        RenderLine(A, B, color, depthTested);
                        RenderLine(B, C, color, depthTested);
                        RenderLine(C, A, color, depthTested);
                    }
                }

                /// <summary>
                /// Renders a circle.
                /// Default position is aligned with the Y-plane.
                /// </summary>
                /// <param name="centerPosition"></param>
                /// <param name="rotation"></param>
                /// <param name="radius"></param>
                /// <param name="solid"></param>
                /// <param name="color"></param>
                public static void RenderCircle(
                    Vector3 centerPosition,
                    Quaternion rotation,
                    float radius,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Circle,
                        Matrix4x4.CreateFromQuaternion(rotation),
                        centerPosition,
                        Vector3.Zero,
                        Rotator.GetZero(),
                        Vector2.Zero,
                        radius,
                        0.0f,
                        solid,
                        depthTested,
                        color));

                private static void RenderCircleImmediate(
                    Vector3 centerPosition,
                    Quaternion rotation,
                    float radius,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = DebugPrimitiveTopologyCache.CircleSegments;
                    ReadOnlySpan<Vector2> unitCircle = DebugPrimitiveTopologyCache.UnitCircle;

                    if (solid)
                    {
                        // Generate circle points for a triangle fan.
                        Span<Vector3> circlePoints = stackalloc Vector3[segments + 1];
                        for (int i = 0; i <= segments; i++)
                        {
                            float x = unitCircle[i].X * radius;
                            float z = unitCircle[i].Y * radius;
                            Vector3 localPoint = new(x, 0, z);
                            circlePoints[i] = Vector3.Transform(localPoint, rotation) + centerPosition;
                        }

                        // Build triangle fan: center + each adjacent edge.
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 pos0 = centerPosition;
                            Vector3 pos1 = circlePoints[i];
                            Vector3 pos2 = circlePoints[i + 1];
                            RenderTriangle(pos0, pos1, pos2, color, true);
                        }
                    }
                    else
                    {
                        // Render circle outline using lines.
                        Span<Vector3> circlePoints = stackalloc Vector3[segments + 1];
                        for (int i = 0; i <= segments; i++)
                        {
                            float x = unitCircle[i].X * radius;
                            float z = unitCircle[i].Y * radius;
                            Vector3 localPoint = new Vector3(x, 0, z);
                            circlePoints[i] = Vector3.Transform(localPoint, rotation) + centerPosition;
                        }
                        for (int i = 0; i < segments; i++)
                            RenderLine(circlePoints[i], circlePoints[i + 1], color);
                    }
                }

                public static void RenderQuad(
                    Vector3 centerTranslation,
                    Rotator rotation,
                    Vector2 extents,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Quad,
                        Matrix4x4.Identity,
                        centerTranslation,
                        Vector3.Zero,
                        rotation,
                        extents,
                        0.0f,
                        0.0f,
                        solid,
                        depthTested,
                        color));

                private static void RenderQuadImmediate(
                    Vector3 centerTranslation,
                    Rotator rotation,
                    Vector2 extents,
                    bool solid,
                    ColorF4 color)
                {
                    if (solid)
                    {
                        Span<Vector3> quadPoints = stackalloc Vector3[4];
                        quadPoints[0] = new Vector3(-extents.X, 0, -extents.Y);
                        quadPoints[1] = new Vector3(extents.X, 0, -extents.Y);
                        quadPoints[2] = new Vector3(extents.X, 0, extents.Y);
                        quadPoints[3] = new Vector3(-extents.X, 0, extents.Y);
                        Matrix4x4 rotMatrix = rotation.GetMatrix();
                        for (int i = 0; i < 4; i++)
                            quadPoints[i] = Vector3.Transform(quadPoints[i], rotMatrix) + centerTranslation;
                        RenderTriangle(quadPoints[0], quadPoints[1], quadPoints[2], color, true);
                        RenderTriangle(quadPoints[0], quadPoints[2], quadPoints[3], color, true);
                    }
                    else
                    {
                        Span<Vector3> quadPoints = stackalloc Vector3[4];
                        quadPoints[0] = new Vector3(-extents.X, 0, -extents.Y);
                        quadPoints[1] = new Vector3(extents.X, 0, -extents.Y);
                        quadPoints[2] = new Vector3(extents.X, 0, extents.Y);
                        quadPoints[3] = new Vector3(-extents.X, 0, extents.Y);
                        Matrix4x4 rotMatrix = rotation.GetMatrix();
                        for (int i = 0; i < 4; i++)
                            quadPoints[i] = Vector3.Transform(quadPoints[i], rotMatrix) + centerTranslation;
                        RenderLine(quadPoints[0], quadPoints[1], color);
                        RenderLine(quadPoints[1], quadPoints[2], color);
                        RenderLine(quadPoints[2], quadPoints[3], color);
                        RenderLine(quadPoints[3], quadPoints[0], color);
                    }
                }

                public static void RenderSphere(
                    Vector3 center,
                    float radius,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Sphere,
                        Matrix4x4.Identity,
                        center,
                        Vector3.Zero,
                        Rotator.GetZero(),
                        Vector2.Zero,
                        radius,
                        0.0f,
                        solid,
                        depthTested,
                        color));

                private static void RenderSphereImmediate(
                    Vector3 center,
                    float radius,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = DebugPrimitiveTopologyCache.SphereSegments;
                    const int rings = DebugPrimitiveTopologyCache.SphereRings;

                    Span<Vector3> spherePoints = stackalloc Vector3[segments * rings];
                    ReadOnlySpan<Vector3> unitSphere = DebugPrimitiveTopologyCache.UnitSphere;
                    for (int i = 0; i < rings; i++)
                    {
                        for (int j = 0; j < segments; j++)
                            spherePoints[i * segments + j] =
                                unitSphere[i * segments + j] * radius + center;
                    }

                    if (solid)
                    {
                        // Build triangle fan: center + each adjacent edge.
                        for (int i = 0; i < rings - 1; i++)
                        {
                            for (int j = 0; j < segments; j++)
                            {
                                int next = (j + 1) % segments;
                                Vector3 pos0 = spherePoints[i * segments + j];
                                Vector3 pos1 = spherePoints[i * segments + next];
                                Vector3 pos2 = spherePoints[(i + 1) * segments + j];
                                RenderTriangle(pos0, pos1, pos2, color, true);

                                pos0 = spherePoints[i * segments + next];
                                pos1 = spherePoints[(i + 1) * segments + next];
                                pos2 = spherePoints[(i + 1) * segments + j];
                                RenderTriangle(pos0, pos1, pos2, color, true);
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < rings; i++)
                        {
                            for (int j = 0; j < segments; j++)
                            {
                                Vector3 pos0 = spherePoints[i * segments + j];
                                Vector3 pos1 = spherePoints[i * segments + (j + 1) % segments];
                                RenderLine(pos0, pos1, color);
                                if (i < rings - 1)
                                {
                                    Vector3 pos2 = spherePoints[(i + 1) * segments + j];
                                    RenderLine(pos0, pos2, color);
                                }
                            }
                        }
                    }
                }

                public static void RenderRect2D(
                    BoundingRectangleF bounds,
                    bool solid,
                    ColorF4 color)
                    => RenderQuad(
                        new Vector3(bounds.Center.X, bounds.Center.Y, 0.0f),
                        Rotator.GetZero(),
                        new Vector2(bounds.Extents.X, bounds.Extents.Y),
                        solid,
                        color);

                public static void RenderAABB(
                    Vector3 halfExtents,
                    Vector3 translation,
                    bool solid,
                    ColorF4 color)
                    => RenderBox(
                        halfExtents,
                        translation,
                        Matrix4x4.Identity,
                        solid,
                        color);

                /// <summary>
                /// Renders a wireframe box in the dedicated BVH base or highlight layer.
                /// Base overlays are drawn before ordinary debug geometry; highlights are drawn after it.
                /// </summary>
                public static void RenderOverlayBox(
                    Vector3 halfExtents,
                    Vector3 center,
                    Matrix4x4 transform,
                    ColorF4 color,
                    float lineWidth,
                    BvhDebugOverlayLayer overlayLayer)
                {
                    Span<Vector3> boxPoints = stackalloc Vector3[8];
                    FillBoxPoints(boxPoints, halfExtents, center, transform);

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    DebugLineOverlayState overlay = overlayLayer == BvhDebugOverlayLayer.Highlight
                        ? scene.HighlightOverlayLines
                        : scene.BaseOverlayLines;
                    overlay.AddBox(boxPoints, color, lineWidth);
                }

                public static void RenderBox(
                    Vector3 halfExtents,
                    Vector3 center,
                    Matrix4x4 transform,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                {
                    Span<Vector3> boxPoints = stackalloc Vector3[8];
                    FillBoxPoints(boxPoints, halfExtents, center, transform);

                    if (solid)
                    {
                        RenderTriangle(boxPoints[0], boxPoints[1], boxPoints[2], color, true, depthTested);
                        RenderTriangle(boxPoints[0], boxPoints[2], boxPoints[3], color, true, depthTested);
                        RenderTriangle(boxPoints[4], boxPoints[5], boxPoints[6], color, true, depthTested);
                        RenderTriangle(boxPoints[4], boxPoints[6], boxPoints[7], color, true, depthTested);
                        RenderTriangle(boxPoints[0], boxPoints[1], boxPoints[5], color, true, depthTested);
                        RenderTriangle(boxPoints[0], boxPoints[5], boxPoints[4], color, true, depthTested);
                        RenderTriangle(boxPoints[2], boxPoints[3], boxPoints[7], color, true, depthTested);
                        RenderTriangle(boxPoints[2], boxPoints[7], boxPoints[6], color, true, depthTested);
                        RenderTriangle(boxPoints[1], boxPoints[2], boxPoints[6], color, true, depthTested);
                        RenderTriangle(boxPoints[1], boxPoints[6], boxPoints[5], color, true, depthTested);
                        RenderTriangle(boxPoints[0], boxPoints[3], boxPoints[7], color, true, depthTested);
                        RenderTriangle(boxPoints[0], boxPoints[7], boxPoints[4], color, true, depthTested);
                    }
                    else
                    {
                        RenderLine(boxPoints[0], boxPoints[1], color);
                        RenderLine(boxPoints[1], boxPoints[2], color);
                        RenderLine(boxPoints[2], boxPoints[3], color);
                        RenderLine(boxPoints[3], boxPoints[0], color);
                        RenderLine(boxPoints[4], boxPoints[5], color);
                        RenderLine(boxPoints[5], boxPoints[6], color);
                        RenderLine(boxPoints[6], boxPoints[7], color);
                        RenderLine(boxPoints[7], boxPoints[4], color);
                        RenderLine(boxPoints[0], boxPoints[4], color);
                        RenderLine(boxPoints[1], boxPoints[5], color);
                        RenderLine(boxPoints[2], boxPoints[6], color);
                        RenderLine(boxPoints[3], boxPoints[7], color);
                    }
                }

                private static void FillBoxPoints(
                    Span<Vector3> points,
                    Vector3 halfExtents,
                    Vector3 center,
                    Matrix4x4 transform)
                {
                    points[0] = new(-halfExtents.X, -halfExtents.Y, -halfExtents.Z);
                    points[1] = new(halfExtents.X, -halfExtents.Y, -halfExtents.Z);
                    points[2] = new(halfExtents.X, -halfExtents.Y, halfExtents.Z);
                    points[3] = new(-halfExtents.X, -halfExtents.Y, halfExtents.Z);
                    points[4] = new(-halfExtents.X, halfExtents.Y, -halfExtents.Z);
                    points[5] = new(halfExtents.X, halfExtents.Y, -halfExtents.Z);
                    points[6] = new(halfExtents.X, halfExtents.Y, halfExtents.Z);
                    points[7] = new(-halfExtents.X, halfExtents.Y, halfExtents.Z);

                    for (int i = 0; i < points.Length; i++)
                        points[i] = Vector3.Transform(points[i], transform) + center;
                }

                public static void RenderCapsule(
                    Capsule capsule,
                    ColorF4 color)
                    => RenderCapsule(
                        capsule.Center,
                        capsule.UpAxis,
                        capsule.Radius,
                        capsule.HalfHeight,
                        false,
                        color);

                public static void RenderCapsule(
                    Vector3 start,
                    Vector3 end,
                    float radius,
                    bool solid,
                    ColorF4 color)
                    => RenderCapsule(
                        (start + end) * 0.5f,
                        (end - start).Normalized(),
                        radius,
                        Vector3.Distance(start, end) * 0.5f,
                        solid,
                        color);

                public static void RenderCapsule(
                    Vector3 center,
                    Vector3 localUpAxis,
                    float radius,
                    float halfHeight,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Capsule,
                        Matrix4x4.Identity,
                        center,
                        localUpAxis,
                        Rotator.GetZero(),
                        Vector2.Zero,
                        radius,
                        halfHeight,
                        solid,
                        depthTested,
                        color));

                private static void RenderCapsuleImmediate(
                    Vector3 center,
                    Vector3 localUpAxis,
                    float radius,
                    float halfHeight,
                    bool solid,
                    ColorF4 color)
                {
                    if (solid)
                    {
                        const int segments = 10;
                        const int rings = 10;

                        Span<Vector3> capsulePoints = stackalloc Vector3[segments * rings];
                        for (int i = 0; i < rings; i++)
                        {
                            float theta = MathF.PI * i / rings;
                            float sinTheta = MathF.Sin(theta);
                            float cosTheta = MathF.Cos(theta);
                            for (int j = 0; j < segments; j++)
                            {
                                float phi = 2 * MathF.PI * j / segments;
                                float sinPhi = MathF.Sin(phi);
                                float cosPhi = MathF.Cos(phi);
                                Vector3 localPoint = new(cosPhi * sinTheta, cosTheta, sinPhi * sinTheta);
                                capsulePoints[i * segments + j] = localPoint * radius + center + localUpAxis * halfHeight;
                            }
                        }

                        for (int i = 0; i < rings; i++)
                        {
                            for (int j = 0; j < segments; j++)
                            {
                                int next = (j + 1) % segments;
                                int nextRing = i < rings - 1 ? i + 1 : i;
                                Vector3 pos0 = capsulePoints[i * segments + j];
                                Vector3 pos1 = capsulePoints[i * segments + next];
                                RenderLine(pos0, pos1, color);
                                if (i < rings - 1)
                                {
                                    Vector3 pos2 = capsulePoints[nextRing * segments + j];
                                    RenderLine(pos0, pos2, color);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Render a wireframe capsule using circles and lines
                        const int segments = DebugPrimitiveTopologyCache.CircleSegments;
                        const int arcSegments = 8; // Number of segments for the hemisphere arcs

                        // Compute orthonormal basis for drawing circles perpendicular to localUpAxis
                        Vector3 tangent = Vector3.Normalize(Vector3.Cross(localUpAxis,
                            localUpAxis.Equals(Vector3.UnitX) ? Vector3.UnitY : Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(localUpAxis, tangent);

                        Vector3 topCenter = center + localUpAxis * halfHeight;
                        Vector3 bottomCenter = center - localUpAxis * halfHeight;

                        // Build circle points at top and bottom (reused for arcs)
                        Span<Vector3> circlePoints = stackalloc Vector3[segments];
                        ReadOnlySpan<Vector2> unitCircle = DebugPrimitiveTopologyCache.UnitCircle;
                        for (int i = 0; i < segments; i++)
                        {
                            float cos = unitCircle[i].X;
                            float sin = unitCircle[i].Y;
                            Vector3 offset = tangent * cos * radius + bitangent * sin * radius;
                            circlePoints[i] = offset; // Store just the offset for reuse
                        }

                        // Draw top and bottom circles
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 point1 = topCenter + circlePoints[i];
                            Vector3 point2 = topCenter + circlePoints[(i + 1) % segments];
                            RenderLine(point1, point2, color);

                            point1 = bottomCenter + circlePoints[i];
                            point2 = bottomCenter + circlePoints[(i + 1) % segments];
                            RenderLine(point1, point2, color);
                        }

                        // Draw vertical lines and hemisphere arcs
                        // Use fewer vertical lines to avoid cluttering
                        int verticalLineCount = 4;
                        for (int i = 0; i < segments; i += segments / verticalLineCount)
                        {
                            // Draw vertical lines connecting top and bottom circles
                            Vector3 topPoint = topCenter + circlePoints[i];
                            Vector3 bottomPoint = bottomCenter + circlePoints[i];
                            RenderLine(topPoint, bottomPoint, color);

                            // Draw hemisphere arcs
                            // Top hemisphere
                            Vector3 prevPoint = topPoint;
                            for (int j = 1; j <= arcSegments; j++)
                            {
                                float t = j / (float)arcSegments;
                                float arcAngle = MathF.PI / 2 * t;
                                float cosArc = MathF.Cos(arcAngle);
                                float sinArc = MathF.Sin(arcAngle);

                                Vector3 newPoint = topCenter +
                                    circlePoints[i] * cosArc +
                                    localUpAxis * radius * sinArc;

                                RenderLine(prevPoint, newPoint, color);
                                prevPoint = newPoint;
                            }

                            // Bottom hemisphere
                            prevPoint = bottomPoint;
                            for (int j = 1; j <= arcSegments; j++)
                            {
                                float t = j / (float)arcSegments;
                                float arcAngle = MathF.PI / 2 * t;
                                float cosArc = MathF.Cos(arcAngle);
                                float sinArc = MathF.Sin(arcAngle);

                                Vector3 newPoint = bottomCenter +
                                    circlePoints[i] * cosArc -
                                    localUpAxis * radius * sinArc;

                                RenderLine(prevPoint, newPoint, color);
                                prevPoint = newPoint;
                            }
                        }
                    }
                }

                public static void RenderCylinder(
                    Matrix4x4 transform,
                    Vector3 localUpAxis,
                    float radius,
                    float halfHeight,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Cylinder,
                        transform,
                        Vector3.Zero,
                        localUpAxis,
                        Rotator.GetZero(),
                        Vector2.Zero,
                        radius,
                        halfHeight,
                        solid,
                        depthTested,
                        color));

                private static void RenderCylinderImmediate(
                    Matrix4x4 transform,
                    Vector3 localUpAxis,
                    float radius,
                    float halfHeight,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = DebugPrimitiveTopologyCache.CircleSegments;

                    Span<Vector3> cylinderPoints = stackalloc Vector3[segments * 2];
                    ReadOnlySpan<Vector2> unitCircle = DebugPrimitiveTopologyCache.UnitCircle;
                    for (int i = 0; i < segments; i++)
                    {
                        float x = unitCircle[i].X * radius;
                        float z = unitCircle[i].Y * radius;
                        Vector3 localPoint = new(x, 0, z);
                        cylinderPoints[i] = Vector3.Transform(localPoint, transform) + localUpAxis * halfHeight;
                        cylinderPoints[i + segments] = Vector3.Transform(localPoint, transform) - localUpAxis * halfHeight;
                    }

                    if (solid)
                    {
                        // Build triangle fan: center + each adjacent edge.
                        for (int i = 0; i < segments - 1; i++)
                        {
                            Vector3 pos0 = cylinderPoints[i];
                            Vector3 pos1 = cylinderPoints[i + 1];
                            Vector3 pos2 = cylinderPoints[i + segments];
                            RenderTriangle(pos0, pos1, pos2, color, true);
                            pos0 = cylinderPoints[i + 1];
                            pos1 = cylinderPoints[i + segments + 1];
                            pos2 = cylinderPoints[i + segments];
                            RenderTriangle(pos0, pos1, pos2, color, true);
                        }
                        // Build top and bottom caps.
                        Vector3 topCenter = localUpAxis * halfHeight;
                        Vector3 bottomCenter = -localUpAxis * halfHeight;
                        for (int i = 0; i < segments - 1; i++)
                        {
                            Vector3 topPos0 = cylinderPoints[i];
                            Vector3 topPos1 = cylinderPoints[i + 1];
                            Vector3 bottomPos0 = cylinderPoints[i + segments];
                            Vector3 bottomPos1 = cylinderPoints[i + segments + 1];
                            RenderTriangle(topCenter, topPos0, topPos1, color, true);
                            RenderTriangle(bottomCenter, bottomPos0, bottomPos1, color, true);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 pos0 = cylinderPoints[i];
                            Vector3 pos1 = cylinderPoints[(i + 1) % segments];
                            RenderLine(pos0, pos1, color);

                            Vector3 pos2 = cylinderPoints[i + segments];
                            RenderLine(pos0, pos2, color);
                            RenderLine(pos1, pos2, color);
                        }
                    }
                }

                public static void RenderCone(
                    Vector3 baseCenter,
                    Vector3 localUpAxis,
                    float radius,
                    float height,
                    bool solid,
                    ColorF4 color,
                    bool depthTested = false)
                    => SubmitShape(new DebugShapeInstance(
                        EDebugShapeInstanceKind.Cone,
                        Matrix4x4.Identity,
                        baseCenter,
                        localUpAxis,
                        Rotator.GetZero(),
                        Vector2.Zero,
                        radius,
                        height,
                        solid,
                        depthTested,
                        color));

                private static void RenderConeImmediate(
                    Vector3 baseCenter,
                    Vector3 localUpAxis,
                    float radius,
                    float height,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = DebugPrimitiveTopologyCache.CircleSegments;

                    localUpAxis = localUpAxis.LengthSquared() > 1e-12f
                        ? Vector3.Normalize(localUpAxis)
                        : Vector3.UnitY;

                    Vector3 tangentSeed = MathF.Abs(localUpAxis.Y) < 0.99f
                        ? Vector3.UnitY
                        : Vector3.UnitX;
                    Vector3 tangent = Vector3.Normalize(Vector3.Cross(localUpAxis, tangentSeed));
                    Vector3 bitangent = Vector3.Cross(tangent, localUpAxis);
                    Vector3 tip = baseCenter + localUpAxis * height;

                    Span<Vector3> conePoints = stackalloc Vector3[segments + 1];
                    ReadOnlySpan<Vector2> unitCircle = DebugPrimitiveTopologyCache.UnitCircle;
                    for (int i = 0; i <= segments; i++)
                    {
                        Vector3 radial = tangent * (unitCircle[i].X * radius) +
                            bitangent * (unitCircle[i].Y * radius);
                        conePoints[i] = baseCenter + radial;
                    }

                    if (solid)
                    {
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 pos0 = tip;
                            Vector3 pos1 = conePoints[i];
                            Vector3 pos2 = conePoints[i + 1];
                            RenderTriangle(pos0, pos1, pos2, color, true);
                        }
                    }
                    else
                    {
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 pos0 = conePoints[i];
                            Vector3 pos1 = conePoints[i + 1];
                            RenderLine(tip, pos0, color);
                            RenderLine(pos0, pos1, color);
                            RenderLine(pos1, tip, color);
                        }
                    }
                }

                private const float DefaultDebugTextScale = 0.0012f;
                private static readonly ResourcePool<UIText> TextPool = new(() => new());

                public static void RenderText(Vector3 worldPosition, string text, ColorF4 color, float scale = DefaultDebugTextScale)
                {
                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (Engine.IsRenderThread)
                        UpdateDebugText(scene, worldPosition, text, color, scale);
                    else
                    {
                        using (_debugShapeQueueLock.EnterScope())
                            scene.TextUpdateQueue.Enqueue((worldPosition, text, color, scale));
                    }
                }

                private static void UpdateDebugText(DebugPrimitiveSceneState scene, Vector3 worldPosition, string text, ColorF4 color, float scale = DefaultDebugTextScale)
                {
                    int hash = HashCode.Combine(text.GetHashCode(), worldPosition.GetHashCode());
                    scene.Texts.AddOrUpdate(hash, _ =>
                    {
                        UIText t = TextPool.Take();
                        t.Text = text;
                        t.Color = color;
                        t.LocalTranslation = worldPosition;
                        //textObject.FontSize = 1.0f;
                        t.Scale = scale;
                        return (t, Engine.Time.Timer.Time());
                    }, (_, pair) =>
                    {
                        var t = pair.text;
                        t.Text = text;
                        t.Color = color;
                        t.LocalTranslation = worldPosition;
                        t.Scale = scale;
                        t.InvalidateTextMatrix();
                        return pair;
                    });
                }

                public static void RenderFrustum(Frustum frustum, ColorF4 color)
                {
                    RenderLine(frustum.LeftTopNear, frustum.RightTopNear, color);
                    RenderLine(frustum.RightTopNear, frustum.RightBottomNear, color);
                    RenderLine(frustum.RightBottomNear, frustum.LeftBottomNear, color);
                    RenderLine(frustum.LeftBottomNear, frustum.LeftTopNear, color);
                    RenderLine(frustum.LeftTopFar, frustum.RightTopFar, color);
                    RenderLine(frustum.RightTopFar, frustum.RightBottomFar, color);
                    RenderLine(frustum.RightBottomFar, frustum.LeftBottomFar, color);
                    RenderLine(frustum.LeftBottomFar, frustum.LeftTopFar, color);
                    RenderLine(frustum.LeftTopNear, frustum.LeftTopFar, color);
                    RenderLine(frustum.RightTopNear, frustum.RightTopFar, color);
                    RenderLine(frustum.RightBottomNear, frustum.RightBottomFar, color);
                    RenderLine(frustum.LeftBottomNear, frustum.LeftBottomFar, color);
                }

                public static void RenderShape(IShape shape, bool solid, ColorF4 color)
                {
                    switch (shape)
                    {
                        case Sphere s:
                            RenderSphere(s.Center, s.Radius, solid, color);
                            break;
                        case AABB a:
                            RenderAABB(a.HalfExtents, a.Center, solid, color);
                            break;
                        case Box b:
                            Matrix4x4 orientation = b.Transform;
                            orientation.M41 = 0.0f;
                            orientation.M42 = 0.0f;
                            orientation.M43 = 0.0f;
                            RenderBox(b.LocalHalfExtents, b.WorldCenter, orientation, solid, color);
                            break;
                        case Capsule c:
                            RenderCapsule(c.Center, c.UpAxis, c.Radius, c.HalfHeight, solid, color);
                            break;
                        //case Cylinder c:
                        //    RenderCylinder(c.Transform, c.LocalUpAxis, c.Radius, c.HalfHeight, solid, color);
                        //    break;
                        case Cone c:
                            RenderCone(c.Center, c.Up, c.Radius, c.Height, solid, color);
                            break;
                    }
                }

                public static void RenderCoordinateSystem(Matrix4x4 space, float lineScale = 1.0f, bool textPerAxis = false)
                {
                    Vector3 x = Vector3.TransformNormal(Vector3.UnitX * lineScale, space);
                    Vector3 y = Vector3.TransformNormal(Vector3.UnitY * lineScale, space);
                    Vector3 z = Vector3.TransformNormal(Vector3.UnitZ * lineScale, space);
                    Vector3 origin = space.Translation;
                    RenderLine(origin, origin + x, ColorF4.Red);
                    RenderLine(origin, origin + y, ColorF4.Green);
                    RenderLine(origin, origin + z, ColorF4.Blue);
                    if (textPerAxis)
                    {
                        RenderText(origin + x * 1.1f, "X", ColorF4.Red);
                        RenderText(origin + y * 1.1f, "Y", ColorF4.Green);
                        RenderText(origin + z * 1.1f, "Z", ColorF4.Blue);
                    }
                }

                public static void RenderCoordinateSystem(Vector3 position, Quaternion rotation, float lineScale = 1.0f, bool textPerAxis = false)
                {
                    Matrix4x4 space = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                    RenderCoordinateSystem(space, lineScale, textPerAxis);
                }

                public static void RenderPlane(Vector3 position, Vector3 normal, ColorF4 color, float size = 1.0f, bool renderNormal = true)
                {
                    // Render a plane as a large quad centered at the position with the given normal.
                    Vector3 up = Vector3.Cross(normal, Vector3.UnitX).Normalized();
                    if (up.LengthSquared() < float.Epsilon)
                        up = Vector3.Cross(normal, Vector3.UnitY).Normalized();
                    Vector3 right = Vector3.Cross(normal, up).Normalized();
                    up = Vector3.Cross(right, normal).Normalized();
                    Vector3 p1 = position + right * size - up * size;
                    Vector3 p2 = position + right * size + up * size;
                    Vector3 p3 = position - right * size + up * size;
                    Vector3 p4 = position - right * size - up * size;
                    RenderLine(p1, p2, color);
                    RenderLine(p2, p3, color);
                    RenderLine(p3, p4, color);
                    RenderLine(p4, p1, color);
                    if (renderNormal)
                        RenderLine(position, position + normal, color);
                }
            }
        }
    }
}
