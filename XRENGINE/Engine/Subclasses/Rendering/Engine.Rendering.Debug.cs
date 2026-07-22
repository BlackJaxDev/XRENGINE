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
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, ColorF4 color)> Lines = [];
                    public readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> Triangles = [];

                    public readonly ConcurrentQueue<(Vector3 pos, ColorF4 color)> PointQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, ColorF4 color)> LineQueue = [];
                    public readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> TriangleQueue = [];

                    public readonly ConcurrentDictionary<int, (UIText text, float lastUpdatedTime)> Texts = new();
                    public readonly ConcurrentQueue<(Vector3 pos, string text, ColorF4 color, float scale)> TextUpdateQueue = [];

                    public readonly InstancedDebugVisualizer Visualizer = new();
                    public readonly DebugLineOverlayState BaseOverlayLines = new();
                    public readonly DebugLineOverlayState HighlightOverlayLines = new();

                    public void ClearQueues()
                    {
                        TextUpdateQueue.Clear();
                        PointQueue.Clear();
                        LineQueue.Clear();
                        TriangleQueue.Clear();
                        BaseOverlayLines.ClearQueues();
                        HighlightOverlayLines.ClearQueues();
                    }

                    public void ClearBags()
                    {
                        Points.Clear();
                        Lines.Clear();
                        Triangles.Clear();
                        BaseOverlayLines.ClearBags();
                        HighlightOverlayLines.ClearBags();
                    }

                    public void ClearVisuals()
                    {
                        Visualizer.Clear();
                        BaseOverlayLines.ClearVisuals();
                        HighlightOverlayLines.ClearVisuals();
                    }
                }

                private static readonly DebugPrimitiveSceneState _debug3D = new();
                private static readonly DebugPrimitiveSceneState _debug2D = new();

                public static void SwapBuffers()
                {
                    using var sample = Engine.Profiler.Start("Rendering.Debug.SwapBuffers");

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
                                PopulateSceneTasks(_debug3D);
                                PopulateSceneTasks(_debug2D);
                                break;
                            case EDebugShapePopulationMode.ParallelInvoke:
                                Parallel.Invoke(
                                    () => PopulateDebugScene(_debug3D),
                                    () => PopulateDebugScene(_debug2D));
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
                    _debug2D.Visualizer.PointSize = debug.DebugPointSize;
                    _debug2D.Visualizer.LineWidth = debug.DebugLineWidth;
                }

                private static void PopulateSceneTasks(DebugPrimitiveSceneState scene)
                {
                    Task tp = Task.Run(() => PopulatePoints(scene));
                    Task tl = Task.Run(() => PopulateLines(scene));
                    Task tt = Task.Run(() => PopulateTriangles(scene));
                    Task.WaitAll(tp, tl, tt);
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

                    int i = 0;
                    foreach (var (pos0, pos1, pos2, color) in scene.Triangles)
                        scene.Visualizer.SetTriangleAt(i++, pos0, pos1, pos2, color);

                    scene.Triangles.Clear();
                }

                private static void PopulateLines(DebugPrimitiveSceneState scene)
                {
                    scene.Visualizer.LineCount = (uint)scene.Lines.Count;

                    int i = 0;
                    foreach (var (pos0, pos1, color) in scene.Lines)
                        scene.Visualizer.SetLineAt(i++, pos0, pos1, color);

                    scene.Lines.Clear();
                }

                private static void PopulatePoints(DebugPrimitiveSceneState scene)
                {
                    scene.Visualizer.PointCount = (uint)scene.Points.Count;

                    int i = 0;
                    foreach (var (pos, color) in scene.Points)
                        scene.Visualizer.SetPointAt(i++, pos, color);

                    scene.Points.Clear();
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
                public static void RenderShapes()
                {
                    if (!Engine.Rendering.State.IsMainPass)
                        return;

                    if (Engine.Rendering.State.RenderingCamera is { } camera &&
                        !camera.CullingMask.Contains(XREngine.Components.Scene.Transforms.DefaultLayers.GizmosIndex))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    DequeueDebugTextItems(scene);

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

                    if (Engine.Rendering.State.DebugInstanceRenderingAvailable)
                    {
                        scene.BaseOverlayLines.Render();
                        scene.Visualizer.Render();
                        scene.HighlightOverlayLines.Render();
                    }
                }

                private static DebugPrimitiveSceneState ResolveDebugPrimitiveSceneState()
                    => ResolveDebugPrimitiveSceneKind() == EDebugPrimitiveSceneKind.Scene2D
                        ? _debug2D
                        : _debug3D;

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
                    while (scene.LineQueue.TryDequeue(out var line))
                        scene.Lines.Add(line);
                    while (scene.TriangleQueue.TryDequeue(out var triangle))
                        scene.Triangles.Add(triangle);
                    scene.BaseOverlayLines.DequeueLines();
                    scene.HighlightOverlayLines.DequeueLines();
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

                public static void RenderPoint(Vector3 position, ColorF4 color)
                {
                    if (!InCamera(position))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (IsRenderThread)
                        scene.Points.Add((position, color));
                    else
                    {
                        using (_debugShapeQueueLock.EnterScope())
                            scene.PointQueue.Enqueue((position, color));
                    }
                }

                public static void RenderRay(Vector3 position, Vector3 direction, ColorF4 color)
                    => RenderLine(position, position + direction, color);

                public static unsafe void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
                {
                    if (!InCamera(start) && !InCamera(end))
                        return;

                    DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                    if (IsRenderThread)
                        scene.Lines.Add((start, end, color));
                    else
                    {
                        using (_debugShapeQueueLock.EnterScope())
                            scene.LineQueue.Enqueue((start, end, color));
                    }
                }

                public static void RenderTriangle(Triangle triangle, ColorF4 color, bool solid)
                    => RenderTriangle(triangle.A, triangle.B, triangle.C, color, solid);
                public static void RenderTriangle(
                    Vector3 A,
                    Vector3 B,
                    Vector3 C,
                    ColorF4 color,
                    bool solid)
                {
                    if (!(InCamera(A) || InCamera(B) || InCamera(C)))
                        return;

                    if (solid)
                    {
                        DebugPrimitiveSceneState scene = ResolveDebugPrimitiveSceneState();
                        if (IsRenderThread)
                            scene.Triangles.Add((A, B, C, color));
                        else
                        {
                            using (_debugShapeQueueLock.EnterScope())
                                scene.TriangleQueue.Enqueue((A, B, C, color));
                        }
                    }
                    else
                    {
                        RenderLine(A, B, color);
                        RenderLine(B, C, color);
                        RenderLine(C, A, color);
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
                    ColorF4 color)
                {
                    const int segments = 20;

                    if (solid)
                    {
                        // Generate circle points for a triangle fan.
                        Vector3[] circlePoints = new Vector3[segments + 1];
                        for (int i = 0; i <= segments; i++)
                        {
                            float angle = 2 * MathF.PI * i / segments;
                            float x = MathF.Cos(angle) * radius;
                            float z = MathF.Sin(angle) * radius;
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
                        Vector3[] circlePoints = new Vector3[segments + 1];
                        for (int i = 0; i <= segments; i++)
                        {
                            float angle = 2 * MathF.PI * i / segments;
                            float x = MathF.Cos(angle) * radius;
                            float z = MathF.Sin(angle) * radius;
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
                    ColorF4 color)
                {
                    if (solid)
                    {
                        Vector3[] quadPoints =
                        [
                            new Vector3(-extents.X, 0, -extents.Y),
                            new Vector3(extents.X, 0, -extents.Y),
                            new Vector3(extents.X, 0, extents.Y),
                            new Vector3(-extents.X, 0, extents.Y),
                        ];
                        Matrix4x4 rotMatrix = rotation.GetMatrix();
                        for (int i = 0; i < 4; i++)
                            quadPoints[i] = Vector3.Transform(quadPoints[i], rotMatrix) + centerTranslation;
                        RenderTriangle(quadPoints[0], quadPoints[1], quadPoints[2], color, true);
                        RenderTriangle(quadPoints[0], quadPoints[2], quadPoints[3], color, true);
                    }
                    else
                    {
                        Vector3[] quadPoints =
                        [
                            new Vector3(-extents.X, 0, -extents.Y),
                            new Vector3(extents.X, 0, -extents.Y),
                            new Vector3(extents.X, 0, extents.Y),
                            new Vector3(-extents.X, 0, extents.Y),
                        ];
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
                    ColorF4 color)
                {
                    const int segments = 20;
                    const int rings = 20;

                    Vector3[] spherePoints = new Vector3[segments * rings];
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
                            spherePoints[i * segments + j] = localPoint * radius + center;
                        }
                    }

                    if (solid)
                    {
                        // Build triangle fan: center + each adjacent edge.
                        for (int i = 0; i < rings - 1; i++)
                        {
                            for (int j = 0; j < segments - 1; j++)
                            {
                                Vector3 pos0 = spherePoints[i * segments + j];
                                Vector3 pos1 = spherePoints[i * segments + j + 1];
                                Vector3 pos2 = spherePoints[(i + 1) * segments + j];
                                RenderTriangle(pos0, pos1, pos2, color, true);

                                pos0 = spherePoints[i * segments + j + 1];
                                pos1 = spherePoints[(i + 1) * segments + j + 1];
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
                    ColorF4 color)
                {
                    Span<Vector3> boxPoints = stackalloc Vector3[8];
                    FillBoxPoints(boxPoints, halfExtents, center, transform);

                    if (solid)
                    {
                        RenderTriangle(boxPoints[0], boxPoints[1], boxPoints[2], color, true);
                        RenderTriangle(boxPoints[0], boxPoints[2], boxPoints[3], color, true);
                        RenderTriangle(boxPoints[4], boxPoints[5], boxPoints[6], color, true);
                        RenderTriangle(boxPoints[4], boxPoints[6], boxPoints[7], color, true);
                        RenderTriangle(boxPoints[0], boxPoints[1], boxPoints[5], color, true);
                        RenderTriangle(boxPoints[0], boxPoints[5], boxPoints[4], color, true);
                        RenderTriangle(boxPoints[2], boxPoints[3], boxPoints[7], color, true);
                        RenderTriangle(boxPoints[2], boxPoints[7], boxPoints[6], color, true);
                        RenderTriangle(boxPoints[1], boxPoints[2], boxPoints[6], color, true);
                        RenderTriangle(boxPoints[1], boxPoints[6], boxPoints[5], color, true);
                        RenderTriangle(boxPoints[0], boxPoints[3], boxPoints[7], color, true);
                        RenderTriangle(boxPoints[0], boxPoints[7], boxPoints[4], color, true);
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
                    ColorF4 color)
                {
                    if (solid)
                    {
                        const int segments = 10;
                        const int rings = 10;

                        Vector3[] capsulePoints = new Vector3[segments * rings];
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
                        const int segments = 16; // Number of segments around the circumference
                        const int arcSegments = 8; // Number of segments for the hemisphere arcs

                        // Compute orthonormal basis for drawing circles perpendicular to localUpAxis
                        Vector3 tangent = Vector3.Normalize(Vector3.Cross(localUpAxis,
                            localUpAxis.Equals(Vector3.UnitX) ? Vector3.UnitY : Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(localUpAxis, tangent);

                        Vector3 topCenter = center + localUpAxis * halfHeight;
                        Vector3 bottomCenter = center - localUpAxis * halfHeight;

                        // Build circle points at top and bottom (reused for arcs)
                        Vector3[] circlePoints = new Vector3[segments];
                        for (int i = 0; i < segments; i++)
                        {
                            float angle = 2 * MathF.PI * i / segments;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
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
                    ColorF4 color)
                {
                    const int segments = 20;

                    Vector3[] cylinderPoints = new Vector3[segments * 2];
                    for (int i = 0; i < segments; i++)
                    {
                        float angle = 2 * MathF.PI * i / segments;
                        float x = MathF.Cos(angle) * radius;
                        float z = MathF.Sin(angle) * radius;
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
                    ColorF4 color)
                {
                    const int segments = 20;

                    localUpAxis = localUpAxis.LengthSquared() > 1e-12f
                        ? Vector3.Normalize(localUpAxis)
                        : Vector3.UnitY;

                    Vector3 tangentSeed = MathF.Abs(localUpAxis.Y) < 0.99f
                        ? Vector3.UnitY
                        : Vector3.UnitX;
                    Vector3 tangent = Vector3.Normalize(Vector3.Cross(localUpAxis, tangentSeed));
                    Vector3 bitangent = Vector3.Cross(tangent, localUpAxis);
                    Vector3 tip = baseCenter + localUpAxis * height;

                    Vector3[] conePoints = new Vector3[segments + 1];
                    for (int i = 0; i <= segments; i++)
                    {
                        float angle = 2 * MathF.PI * i / segments;
                        Vector3 radial = tangent * (MathF.Cos(angle) * radius) +
                            bitangent * (MathF.Sin(angle) * radius);
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
