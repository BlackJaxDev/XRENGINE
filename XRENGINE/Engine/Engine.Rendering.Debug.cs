using Extensions;
using JoltPhysicsSharp;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Core;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Transforms.Rotations;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.UI;
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
                }

                private static void PreUpdate()
                {
                    //lock (DebugTextUpdateQueue)
                        DebugTextUpdateQueue.Clear();
                    
                    DebugPointsQueue.Clear();
                    DebugLinesQueue.Clear();
                    DebugTrianglesQueue.Clear();
                }

                public static readonly Vector3 UIPositionBias = new(0.0f, 0.0f, 0.1f);
                public static readonly Rotator UIRotation = new(90.0f, 0.0f, 0.0f, ERotationOrder.YPR);

                private static readonly ConcurrentBag<(Vector3 pos, ColorF4 color)> _debugPoints = [];
                private static readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, ColorF4 color)> _debugLines = [];
                private static readonly ConcurrentBag<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> _debugTriangles = [];

                private static readonly ConcurrentQueue<(Vector3 pos, ColorF4 color)> DebugPointsQueue = [];
                private static readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, ColorF4 color)> DebugLinesQueue = [];
                private static readonly ConcurrentQueue<(Vector3 pos0, Vector3 pos1, Vector3 pos2, ColorF4 color)> DebugTrianglesQueue = [];

                private static readonly InstancedDebugVisualizer _instancedDebugVisualizer = new();

                public static void SwapBuffers()
                {
                    if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                        return;

                    Task tp = Task.Run(PopulatePoints);
                    Task tl = Task.Run(PopulateLines);
                    Task tt = Task.Run(PopulateTriangles);
                    Task.WaitAll(tp, tl, tt);
                }

                private static void PopulateTriangles()
                {
                    _instancedDebugVisualizer.TriangleCount = (uint)_debugTriangles.Count;

                    int i = 0;
                    foreach (var (pos0, pos1, pos2, color) in _debugTriangles)
                        _instancedDebugVisualizer.SetTriangleAt(i++, pos0, pos1, pos2, color);

                    _debugTriangles.Clear();
                }

                private static void PopulateLines()
                {
                    _instancedDebugVisualizer.LineCount = (uint)_debugLines.Count;

                    int i = 0;
                    foreach (var (pos0, pos1, color) in _debugLines)
                        _instancedDebugVisualizer.SetLineAt(i++, pos0, pos1, color);

                    _debugLines.Clear();
                }

                private static void PopulatePoints()
                {
                    _instancedDebugVisualizer.PointCount = (uint)_debugPoints.Count;

                    int i = 0;
                    foreach (var (pos, color) in _debugPoints)
                        _instancedDebugVisualizer.SetPointAt(i++, pos, color);

                    _debugPoints.Clear();
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

                public static void RenderShapes()
                {
                    //lock (DebugTextUpdateQueue)
                        foreach ((Vector3 pos, string text, ColorF4 color, float scale) in DebugTextUpdateQueue)
                            UpdateDebugText(pos, text, color, scale);

                    var hashes = DebugTexts.Keys.ToArray();
                    for (int i = 0; i < hashes.Length; i++)
                    {
                        var hash = hashes[i];
                        if (!DebugTexts.TryGetValue(hash, out var text))
                            continue;

                        text.text.Render();

                        float nowTime = Engine.Time.Timer.Time();
                        float lastTime = text.lastUpdatedTime;
                        if (nowTime - lastTime > Engine.Rendering.Settings.DebugTextMaxLifespan)
                        {
                            if (DebugTexts.TryRemove(hash, out (UIText text, float lastUpdatedTime) item))
                                TextPool.Release(item.text);
                        }
                    }

                    foreach (var updatePoint in DebugPointsQueue)
                        _debugPoints.Add(updatePoint);
                    foreach (var updateLine in DebugLinesQueue)
                        _debugLines.Add(updateLine);
                    foreach (var updateTriangle in DebugTrianglesQueue)
                        _debugTriangles.Add(updateTriangle);

                    //while (_debugPointsQueue.TryDequeue(out var point))
                    //    _debugPoints.Add(point);
                    //while (_debugLinesQueue.TryDequeue(out var line))
                    //    _debugLines.Add(line);
                    //while (_debugTrianglesQueue.TryDequeue(out var triangle))
                    //    _debugTriangles.Add(triangle);

                    if (!Engine.Rendering.State.DebugInstanceRenderingAvailable)
                    {
                        //var camera = Engine.Rendering.State.RenderingCamera;
                        //var fwd = camera!.Transform.RenderForward;
                        //var up = camera!.Transform.RenderUp;
                        //var right = camera!.Transform.RenderRight;
                        //var pointSize = 0.04f;
                        //Matrix4x4 pointScale = Matrix4x4.CreateScale(pointSize);
                        //foreach (var (pos, color) in _debugPoints)
                        //{
                        //    var rend = GetPointRenderer();
                        //    rend.Material!.SetVector4(0, color);
                        //    rend.Render(pointScale * Matrix4x4.CreateWorld(pos, fwd, up));
                        //}
                        _debugPoints.Clear();
                        //var lineWidth = 0.02f;
                        //foreach (var (pos0, pos1, color) in _debugLines)
                        //{
                        //    var rend = GetLineRenderer();
                        //    rend.Material!.SetVector4(0, color);
                        //    Matrix4x4 matrix = CalculateLineMatrix(pos0, pos1, lineWidth, fwd, up, right);
                        //    rend.Render(matrix);
                        //}
                        _debugLines.Clear();
                        //foreach (var (pos0, pos1, pos2, color) in _debugTriangles)
                        //{

                        //}
                        _debugTriangles.Clear();
                    }
                    else
                    {
                        SwapBuffers();
                        _instancedDebugVisualizer.Render();
                    }
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

                    if (IsRenderThread)
                        _debugPoints.Add((position, color));
                    else
                        DebugPointsQueue.Enqueue((position, color));
                }

                public static unsafe void RenderLine(Vector3 start, Vector3 end, ColorF4 color)
                {
                    if (!InCamera(start) && !InCamera(end))
                        return;
                    
                    if (IsRenderThread)
                        _debugLines.Add((start, end, color));
                    else
                        DebugLinesQueue.Enqueue((start, end, color));
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
                        if (IsRenderThread)
                            _debugTriangles.Add((A, B, C, color));
                        else
                            DebugTrianglesQueue.Enqueue((A, B, C, color));
                    }
                    else
                    {
                        RenderLine(A, B, color);
                        RenderLine(B, C, color);
                        RenderLine(C, A, color);
                    }
                }

                public static void RenderCircle(
                    Vector3 centerPosition,
                    Rotator rotation,
                    float radius,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = 20;

                    Matrix4x4 rotMatrix = rotation.GetMatrix();
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
                            circlePoints[i] = Vector3.Transform(localPoint, rotMatrix) + centerPosition;
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
                            circlePoints[i] = Vector3.Transform(localPoint, rotMatrix) + centerPosition;
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

                public static void RenderBox(
                    Vector3 halfExtents,
                    Vector3 center,
                    Matrix4x4 transform,
                    bool solid,
                    ColorF4 color)
                {
                    Vector3[] boxPoints =
                    {
                        new(-halfExtents.X, -halfExtents.Y, -halfExtents.Z),
                        new(halfExtents.X, -halfExtents.Y, -halfExtents.Z),
                        new(halfExtents.X, -halfExtents.Y, halfExtents.Z),
                        new(-halfExtents.X, -halfExtents.Y, halfExtents.Z),
                        new(-halfExtents.X, halfExtents.Y, -halfExtents.Z),
                        new(halfExtents.X, halfExtents.Y, -halfExtents.Z),
                        new(halfExtents.X, halfExtents.Y, halfExtents.Z),
                        new(-halfExtents.X, halfExtents.Y, halfExtents.Z),
                    };

                    for (int i = 0; i < 8; i++)
                        boxPoints[i] = Vector3.Transform(boxPoints[i], transform) + center;

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
                    Vector3 center,
                    Vector3 localUpAxis,
                    float radius,
                    float height,
                    bool solid,
                    ColorF4 color)
                {
                    const int segments = 20;

                    Vector3[] conePoints = new Vector3[segments + 1];
                    for (int i = 0; i <= segments; i++)
                    {
                        float angle = 2 * MathF.PI * i / segments;
                        float x = MathF.Cos(angle) * radius;
                        float z = MathF.Sin(angle) * radius;
                        Vector3 localPoint = new(x, 0, z);
                        conePoints[i] = localPoint + center + localUpAxis * height;
                    }

                    if (solid)
                    {
                        // Build triangle fan: center + each adjacent edge.
                        for (int i = 0; i < segments; i++)
                        {
                            Vector3 pos0 = center;
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
                            RenderLine(center, pos0, color);
                            RenderLine(pos0, pos1, color);
                            RenderLine(pos1, center, color);
                        }
                    }
                }

                private static readonly ConcurrentDictionary<int, (UIText text, float lastUpdatedTime)> DebugTexts = new();
                private static readonly ResourcePool<UIText> TextPool = new(() => new());
                private static readonly ConcurrentQueue<(Vector3 pos, string text, ColorF4 color, float scale)> DebugTextUpdateQueue = new();

                public static void RenderText(Vector3 bestPoint, string text, ColorF4 color, float scale = 0.0001f)
                {
                    if (Engine.IsRenderThread)
                        UpdateDebugText(bestPoint, text, color);
                    else
                    {
                        //lock (DebugTextUpdateQueue)
                            DebugTextUpdateQueue.Enqueue((bestPoint, text, color, scale));
                    }
                }

                private static void UpdateDebugText(Vector3 bestPoint, string text, ColorF4 color, float scale = 0.001f)
                {
                    int hash = HashCode.Combine(text.GetHashCode(), bestPoint.GetHashCode());
                    DebugTexts.AddOrUpdate(hash, _ =>
                    {
                        UIText t = TextPool.Take();
                        t.Text = text;
                        t.Color = color;
                        t.Translation = bestPoint;
                        //textObject.FontSize = 1.0f;
                        t.Scale = scale;
                        return (t, Engine.Time.Timer.Time());
                    }, (_, pair) =>
                    {
                        var t = pair.text;
                        t.Text = text;
                        t.Color = color;
                        t.Translation = bestPoint;
                        t.Scale = scale;
                        t.UpdateTextMatrix();
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
                            RenderBox(b.LocalHalfExtents, b.LocalCenter, b.Transform, solid, color);
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
                    Vector3 x = space.GetColumn(0).XYZ() * lineScale;
                    Vector3 y = space.GetColumn(1).XYZ() * lineScale;
                    Vector3 z = space.GetColumn(2).XYZ() * lineScale;
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
            }
        }
    }
}