using MagicPhysX;
using XREngine.Scene.Physics.DebugVisualization;

namespace XREngine.Scene.Physics.Physx;

/// <summary>
/// Narrow PhysX-to-engine conversion layer. Native pointers are accepted only for
/// the duration of this synchronous copy and are never retained.
/// </summary>
internal static unsafe class PhysxDebugFrameAdapter
{
    public static void Copy(
        PxDebugPoint* points,
        int pointCount,
        PxDebugLine* lines,
        int lineCount,
        PxDebugTriangle* triangles,
        int triangleCount,
        PhysicsDebugFrameWriter writer)
    {
        for (int index = 0; index < pointCount; index++)
        {
            ref PxDebugPoint point = ref points[index];
            writer.AddPoint(new PhysicsDebugPoint((System.Numerics.Vector3)point.pos, point.color));
        }

        for (int index = 0; index < lineCount; index++)
        {
            ref PxDebugLine line = ref lines[index];
            writer.AddLine(new PhysicsDebugLine(
                (System.Numerics.Vector3)line.pos0,
                (System.Numerics.Vector3)line.pos1,
                line.color0));
        }

        for (int index = 0; index < triangleCount; index++)
        {
            ref PxDebugTriangle triangle = ref triangles[index];
            writer.AddTriangle(new PhysicsDebugTriangle(
                (System.Numerics.Vector3)triangle.pos0,
                (System.Numerics.Vector3)triangle.pos1,
                (System.Numerics.Vector3)triangle.pos2,
                triangle.color0));
        }
    }
}
