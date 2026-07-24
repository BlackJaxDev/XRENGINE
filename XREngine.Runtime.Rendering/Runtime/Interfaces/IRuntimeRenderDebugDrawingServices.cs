using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Data.Transforms.Rotations;
using XREngine.Input;
using XREngine.Rendering.API.Rendering.OpenXR;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.Shadows;
using XREngine.Rendering.Vulkan;
using XREngine.Scene;

namespace XREngine.Rendering;

/// <summary>
/// Optional debug drawing and scene-maintenance instrumentation.
/// </summary>
public interface IRuntimeRenderDebugDrawingServices
{

    /// <summary>
    /// Gets whether the 3D CPU spatial tree debug visualization is enabled by host editor preferences.
    /// </summary>
    bool Preview3DWorldOctree { get; }

    /// <summary>
    /// Gets whether the 2D quadtree debug visualization is enabled by host editor preferences.
    /// </summary>
    bool Preview2DWorldQuadtree { get; }

    /// <summary>
    /// Gets whether hover outlines are enabled by host editor preferences.
    /// </summary>
    bool HoverOutlineEnabled { get; }

    /// <summary>
    /// Gets whether selection outlines are enabled by host editor preferences.
    /// </summary>
    bool SelectionOutlineEnabled { get; }

    /// <summary>
    /// Gets the host theme color for octree or BVH nodes intersecting the active culling volume.
    /// </summary>
    ColorF4 OctreeIntersectedBoundsColor { get; }

    /// <summary>
    /// Gets the host theme color for octree or BVH nodes contained by the active culling volume.
    /// </summary>
    ColorF4 OctreeContainedBoundsColor { get; }

    /// <summary>
    /// Gets the host theme color for quadtree nodes intersecting the active culling volume.
    /// </summary>
    ColorF4 QuadtreeIntersectedBoundsColor { get; }

    /// <summary>
    /// Gets the host theme color for quadtree nodes contained by the active culling volume.
    /// </summary>
    ColorF4 QuadtreeContainedBoundsColor { get; }

    /// <summary>
    /// Pushes the currently rendered transform identifier for diagnostics and debug visualization.
    /// </summary>
    IDisposable? PushTransformId(uint transformId);

    /// <summary>
    /// Records that an octree move was skipped during runtime visibility maintenance.
    /// </summary>
    void RecordOctreeSkippedMove();

    /// <summary>
    /// Processes pending GPU physics chain dispatch requests before scene rendering.
    /// </summary>
    void ProcessGpuPhysicsChainDispatches();

    /// <summary>
    /// Processes completed GPU physics chain readbacks or completion callbacks after scene work.
    /// </summary>
    void ProcessGpuPhysicsChainCompletions();

    /// <summary>
    /// Queues or renders a two-dimensional debug rectangle.
    /// </summary>
    void RenderDebugRect2D(BoundingRectangleF rectangle, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug line.
    /// </summary>
    void RenderDebugLine(Vector3 start, Vector3 end, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug sphere.
    /// </summary>
    void RenderDebugSphere(Vector3 center, float radius, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug cone.
    /// </summary>
    void RenderDebugCone(Vector3 center, Vector3 up, float radius, float height, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an axis-aligned debug bounding box.
    /// </summary>
    void RenderDebugAABB(Vector3 halfExtents, Vector3 center, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an oriented debug bounding box.
    /// </summary>
    void RenderDebugBox(Vector3 halfExtents, Vector3 center, Matrix4x4 transform, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders an oriented debug quad.
    /// </summary>
    void RenderDebugQuad(Vector3 center, Rotator rotation, Vector2 extents, bool solid, ColorF4 color);

    /// <summary>
    /// Queues or renders a world-space debug point.
    /// </summary>
    void RenderDebugPoint(Vector3 position, ColorF4 color);

    /// <summary>
    /// Queues or renders world-space debug text.
    /// </summary>
    void RenderDebugText(Vector3 position, string text, ColorF4 color);

    /// <summary>
    /// Flushes any queued debug shapes through the host debug renderer.
    /// </summary>
    void RenderDebugShapes(bool depthTested);
}
