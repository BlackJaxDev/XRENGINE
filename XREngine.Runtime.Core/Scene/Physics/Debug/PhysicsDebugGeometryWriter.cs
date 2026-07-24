using System.Numerics;

namespace XREngine.Scene.Physics.DebugVisualization;

/// <summary>
/// Allocation-free fallback tessellation for backend debug callbacks that expose
/// analytic markers instead of point/line/triangle batches.
/// </summary>
public static class PhysicsDebugGeometryWriter
{
    private const int CircleSegments = 16;
    private static readonly (int Start, int End)[] BoxEdges =
    [
        (0, 1), (1, 3), (3, 2), (2, 0),
        (4, 5), (5, 7), (7, 6), (6, 4),
        (0, 4), (1, 5), (2, 6), (3, 7),
    ];

    public static void AddSphere(
        PhysicsDebugFrameWriter writer,
        Vector3 center,
        float radius,
        uint color)
    {
        AddCircle(writer, center, Vector3.UnitX, Vector3.UnitY, radius, color);
        AddCircle(writer, center, Vector3.UnitY, Vector3.UnitZ, radius, color);
        AddCircle(writer, center, Vector3.UnitZ, Vector3.UnitX, radius, color);
    }

    public static void AddCapsule(
        PhysicsDebugFrameWriter writer,
        Vector3 start,
        Vector3 end,
        float radius,
        uint color)
    {
        Vector3 axis = end - start;
        if (axis.LengthSquared() <= float.Epsilon)
        {
            AddSphere(writer, start, radius, color);
            return;
        }

        axis = Vector3.Normalize(axis);
        Vector3 tangent = Vector3.Normalize(
            MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) < 0.95f
                ? Vector3.Cross(axis, Vector3.UnitY)
                : Vector3.Cross(axis, Vector3.UnitX));
        Vector3 bitangent = Vector3.Cross(axis, tangent);

        AddCircle(writer, start, tangent, bitangent, radius, color);
        AddCircle(writer, end, tangent, bitangent, radius, color);

        for (int index = 0; index < 4; index++)
        {
            float angle = index * MathF.PI * 0.5f;
            Vector3 radial = (tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * radius;
            writer.AddLine(new PhysicsDebugLine(start + radial, end + radial, color));
        }

        AddHemisphereArcs(writer, start, -axis, tangent, bitangent, radius, color);
        AddHemisphereArcs(writer, end, axis, tangent, bitangent, radius, color);
    }

    public static void AddBox(
        PhysicsDebugFrameWriter writer,
        Vector3 center,
        Quaternion rotation,
        Vector3 halfExtents,
        uint color)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int index = 0; index < corners.Length; index++)
        {
            Vector3 local = new(
                (index & 1) == 0 ? -halfExtents.X : halfExtents.X,
                (index & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                (index & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
            corners[index] = center + Vector3.Transform(local, rotation);
        }

        foreach ((int start, int end) in BoxEdges)
            writer.AddLine(new PhysicsDebugLine(corners[start], corners[end], color));
    }

    public static void AddAabb(
        PhysicsDebugFrameWriter writer,
        Vector3 minimum,
        Vector3 maximum,
        uint color)
        => AddBox(
            writer,
            (minimum + maximum) * 0.5f,
            Quaternion.Identity,
            (maximum - minimum) * 0.5f,
            color);

    public static void AddAxes(
        PhysicsDebugFrameWriter writer,
        Vector3 origin,
        Quaternion rotation,
        float length)
    {
        writer.AddLine(new PhysicsDebugLine(
            origin,
            origin + Vector3.Transform(Vector3.UnitX * length, rotation),
            0xFF0000FFu));
        writer.AddLine(new PhysicsDebugLine(
            origin,
            origin + Vector3.Transform(Vector3.UnitY * length, rotation),
            0xFF00FF00u));
        writer.AddLine(new PhysicsDebugLine(
            origin,
            origin + Vector3.Transform(Vector3.UnitZ * length, rotation),
            0xFFFF0000u));
    }

    private static void AddCircle(
        PhysicsDebugFrameWriter writer,
        Vector3 center,
        Vector3 axisA,
        Vector3 axisB,
        float radius,
        uint color)
    {
        Vector3 previous = center + axisA * radius;
        for (int index = 1; index <= CircleSegments; index++)
        {
            float angle = index * (MathF.Tau / CircleSegments);
            Vector3 current = center
                + (axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle)) * radius;
            writer.AddLine(new PhysicsDebugLine(previous, current, color));
            previous = current;
        }
    }

    private static void AddHemisphereArcs(
        PhysicsDebugFrameWriter writer,
        Vector3 center,
        Vector3 pole,
        Vector3 tangent,
        Vector3 bitangent,
        float radius,
        uint color)
    {
        AddHemisphereArc(writer, center, pole, tangent, radius, color);
        AddHemisphereArc(writer, center, pole, -tangent, radius, color);
        AddHemisphereArc(writer, center, pole, bitangent, radius, color);
        AddHemisphereArc(writer, center, pole, -bitangent, radius, color);
    }

    private static void AddHemisphereArc(
        PhysicsDebugFrameWriter writer,
        Vector3 center,
        Vector3 pole,
        Vector3 radial,
        float radius,
        uint color)
    {
        const int arcSegments = CircleSegments / 4;
        Vector3 previous = center + radial * radius;
        for (int index = 1; index <= arcSegments; index++)
        {
            float angle = index * (MathF.PI * 0.5f / arcSegments);
            Vector3 current = center
                + (radial * MathF.Cos(angle) + pole * MathF.Sin(angle)) * radius;
            writer.AddLine(new PhysicsDebugLine(previous, current, color));
            previous = current;
        }
    }
}
