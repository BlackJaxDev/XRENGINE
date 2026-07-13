using JoltPhysicsSharp;
using System.Numerics;

namespace XREngine.Scene.Physics.Jolt;

/// <summary>
/// Converts backend-neutral collider authoring into native Jolt shapes while preserving
/// PhysX mesh scaling semantics and source triangle identifiers.
/// </summary>
internal static class JoltShapeFactory
{
    private const float MinimumMeshScale = 1.0e-6f;
    private const float MaximumMeshScale = 1.0e6f;

    public static JoltShapeMetadata Create(
        IPhysicsGeometry geometry,
        Vector3 localPosition,
        Quaternion localRotation)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        JoltShapeMetadata metadata = CreateMetadata(geometry);
        return ApplyLocalPose(metadata, localPosition, localRotation);
    }

    public static JoltShapeMetadata? Create(
        IReadOnlyList<PhysicsColliderShape> colliderShapes,
        IPhysicsGeometry? fallbackGeometry,
        Vector3 fallbackPosition,
        Quaternion fallbackRotation)
    {
        ArgumentNullException.ThrowIfNull(colliderShapes);

        int enabledShapeCount = 0;
        int singleShapeIndex = -1;
        for (int index = 0; index < colliderShapes.Count; index++)
        {
            PhysicsColliderShape entry = colliderShapes[index];
            if (!entry.Enabled || entry.Geometry is null)
                continue;

            enabledShapeCount++;
            singleShapeIndex = index;
        }

        if (enabledShapeCount == 0)
        {
            if (fallbackGeometry is null)
                return null;

            return Create(fallbackGeometry, fallbackPosition, fallbackRotation);
        }

        if (enabledShapeCount == 1)
        {
            PhysicsColliderShape entry = colliderShapes[singleShapeIndex];
            return Create(entry.Geometry!, entry.LocalPosition, entry.LocalRotation);
        }

        StaticCompoundShapeSettings settings = new();
        JoltShapeMetadata[] children = new JoltShapeMetadata[enabledShapeCount];
        Vector3[] childPositions = new Vector3[enabledShapeCount];
        Quaternion[] childRotations = new Quaternion[enabledShapeCount];
        int childIndex = 0;
        try
        {
            for (int sourceIndex = 0; sourceIndex < colliderShapes.Count; sourceIndex++)
            {
                PhysicsColliderShape entry = colliderShapes[sourceIndex];
                if (!entry.Enabled || entry.Geometry is null)
                    continue;

                Vector3 localPosition = ValidatePosition(entry.LocalPosition, nameof(entry.LocalPosition));
                Quaternion localRotation = NormalizeRotation(entry.LocalRotation, nameof(entry.LocalRotation));
                JoltShapeMetadata child = CreateMetadata(entry.Geometry);
                children[childIndex++] = child;
                childPositions[childIndex - 1] = localPosition;
                childRotations[childIndex - 1] = localRotation;
                settings.AddShape(localPosition, localRotation, child.Shape, (uint)sourceIndex);
            }

            Shape compound = settings.Create();
            return new JoltCompoundShapeMetadata(compound, children, childPositions, childRotations);
        }
        catch
        {
            for (int index = 0; index < childIndex; index++)
                children[index].Dispose();
            throw;
        }
        finally
        {
            settings.Dispose();
        }
    }

    internal static Shape CreateShape(IPhysicsGeometry geometry)
        => geometry switch
        {
            IPhysicsGeometry.Sphere sphere => new SphereShape(sphere.Radius),
            IPhysicsGeometry.Box box => new BoxShape(box.HalfExtents),
            IPhysicsGeometry.Capsule capsule => new CapsuleShape(capsule.HalfHeight, capsule.Radius),
            IPhysicsGeometry.Plane plane => new PlaneShape(plane.PlaneData),
            PhysicsConvexHullGeometry convex => CreateConvexHullShape(convex),
            PhysicsTriangleMeshGeometry mesh => CreateTriangleMeshShape(mesh),
            PhysicsHeightFieldGeometry heightField => CreateHeightFieldShape(heightField),
            _ => throw new NotSupportedException(
                $"Geometry type '{geometry.GetType().FullName}' has no Jolt adapter. "
                + "Use a backend-neutral authored geometry type for cross-backend content."),
        };

    private static JoltShapeMetadata CreateMetadata(IPhysicsGeometry geometry)
    {
        if (geometry is not PhysicsTriangleMeshGeometry triangleMesh)
            return JoltShapeMetadata.Create(CreateShape(geometry));

        triangleMesh.Validate();
        MeshShape shape = (MeshShape)CreateTriangleMeshShape(triangleMesh);
        Vector3[] transformedVertices = TransformPhysxMeshVertices(
            triangleMesh.Vertices,
            triangleMesh.Scale,
            triangleMesh.ScaleRotation,
            allowNegativeScale: true);
        Dictionary<uint, JoltTriangleVertices> triangles = new(triangleMesh.Indices.Length / 3);
        bool flipWinding = triangleMesh.Scale.X * triangleMesh.Scale.Y * triangleMesh.Scale.Z < 0.0f;
        for (int triangleIndex = 0; triangleIndex < triangleMesh.Indices.Length / 3; triangleIndex++)
        {
            uint i0 = triangleMesh.Indices[triangleIndex * 3];
            uint i1 = triangleMesh.Indices[triangleIndex * 3 + 1];
            uint i2 = triangleMesh.Indices[triangleIndex * 3 + 2];
            if (flipWinding)
                (i1, i2) = (i2, i1);

            uint faceIndex = triangleMesh.SourceFaceIndices.Length == 0
                ? (uint)triangleIndex
                : triangleMesh.SourceFaceIndices[triangleIndex];
            triangles.TryAdd(
                faceIndex,
                new JoltTriangleVertices(
                    transformedVertices[i0],
                    transformedVertices[i1],
                    transformedVertices[i2]));
        }

        return new JoltMeshShapeMetadata(shape, triangles);
    }

    private static Shape CreateConvexHullShape(PhysicsConvexHullGeometry geometry)
    {
        geometry.Validate();
        return CreateConvexHull(geometry.Vertices, geometry.Scale, geometry.ScaleRotation);
    }

    private static Shape CreateTriangleMeshShape(PhysicsTriangleMeshGeometry geometry)
    {
        geometry.Validate();
        return CreateTriangleMesh(
            geometry.Vertices,
            geometry.Indices,
            geometry.SourceFaceIndices,
            geometry.Scale,
            geometry.ScaleRotation);
    }

    private static Shape CreateHeightFieldShape(PhysicsHeightFieldGeometry geometry)
    {
        geometry.Validate();
        float[] samples = new float[geometry.Samples.Length];
        for (int index = 0; index < samples.Length; index++)
            samples[index] = geometry.Samples[index];

        JoltHeightFieldCell[] cells = new JoltHeightFieldCell[geometry.Cells.Length];
        for (int index = 0; index < cells.Length; index++)
        {
            PhysicsHeightFieldCell cell = geometry.Cells[index];
            cells[index] = new JoltHeightFieldCell(
                cell.TessellatedDiagonal,
                cell.LowerTriangleHole,
                cell.UpperTriangleHole);
        }

        return CreateHeightField(
            samples,
            geometry.RowCount,
            geometry.ColumnCount,
            geometry.HeightScale,
            geometry.RowScale,
            geometry.ColumnScale,
            cells);
    }

    public static Shape CreateConvexHull(
        ReadOnlySpan<Vector3> vertices,
        Vector3 scale,
        Quaternion scaleRotation)
    {
        if (vertices.Length < 4)
            throw new ArgumentException("A Jolt convex hull requires at least four vertices.", nameof(vertices));

        Vector3[] transformedVertices = TransformPhysxMeshVertices(
            vertices,
            scale,
            scaleRotation,
            allowNegativeScale: false);

        using ConvexHullShapeSettings settings = new(transformedVertices, 0.0f);
        return settings.Create();
    }

    public static Shape CreateTriangleMesh(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<uint> indices,
        Vector3 scale,
        Quaternion scaleRotation)
        => CreateTriangleMesh(vertices, indices, ReadOnlySpan<uint>.Empty, scale, scaleRotation);

    public static Shape CreateTriangleMesh(
        ReadOnlySpan<Vector3> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<uint> sourceFaceIndices,
        Vector3 scale,
        Quaternion scaleRotation)
    {
        if (vertices.Length < 3)
            throw new ArgumentException("A Jolt triangle mesh requires at least three vertices.", nameof(vertices));
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Triangle indices must contain one or more complete triangles.", nameof(indices));

        int triangleCount = indices.Length / 3;
        if (!sourceFaceIndices.IsEmpty && sourceFaceIndices.Length != triangleCount)
            throw new ArgumentException("Source face indices must match the triangle count.", nameof(sourceFaceIndices));

        Vector3[] transformedVertices = TransformPhysxMeshVertices(
            vertices,
            scale,
            scaleRotation,
            allowNegativeScale: true);
        IndexedTriangle[] triangles = new IndexedTriangle[triangleCount];

        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            uint i0 = indices[triangleIndex * 3];
            uint i1 = indices[triangleIndex * 3 + 1];
            uint i2 = indices[triangleIndex * 3 + 2];
            if (i0 >= transformedVertices.Length || i1 >= transformedVertices.Length || i2 >= transformedVertices.Length)
                throw new ArgumentOutOfRangeException(nameof(indices), $"Triangle {triangleIndex} references a vertex outside the supplied array.");

            // PhysX flips triangle normals for an odd number of reflected axes.
            if (scale.X * scale.Y * scale.Z < 0.0f)
                (i1, i2) = (i2, i1);

            uint sourceFaceIndex = sourceFaceIndices.IsEmpty
                ? (uint)triangleIndex
                : sourceFaceIndices[triangleIndex];
            triangles[triangleIndex] = new IndexedTriangle(in i0, in i1, in i2, 0, sourceFaceIndex);
        }

        using MeshShapeSettings settings = new(transformedVertices.AsSpan(), triangles.AsSpan())
        {
            PerTriangleUserData = true,
        };
        settings.Sanitize();
        return settings.Create();
    }

    public static Shape CreateHeightField(
        ReadOnlySpan<float> heightSamples,
        int rowCount,
        int columnCount,
        float heightScale,
        float rowScale,
        float columnScale,
        ReadOnlySpan<JoltHeightFieldCell> cells)
    {
        if (rowCount < 2)
            throw new ArgumentOutOfRangeException(nameof(rowCount), "A height field requires at least two rows.");
        if (columnCount < 2)
            throw new ArgumentOutOfRangeException(nameof(columnCount), "A height field requires at least two columns.");
        if (heightSamples.Length != checked(rowCount * columnCount))
            throw new ArgumentException("Height sample count does not match the supplied dimensions.", nameof(heightSamples));

        int cellCount = checked((rowCount - 1) * (columnCount - 1));
        if (cells.Length != cellCount)
            throw new ArgumentException("Height-field cell metadata does not match the supplied dimensions.", nameof(cells));

        ValidateHeightScale(heightScale, nameof(heightScale));
        ValidateHeightScale(rowScale, nameof(rowScale));
        ValidateHeightScale(columnScale, nameof(columnScale));

        if (CanUseNativeHeightField(rowCount, columnCount, cells))
        {
            // PhysX stores [row, column], while Jolt indexes [z, x]. Transpose so row
            // remains local X and column remains local Z in both backends.
            float[] joltSamples = new float[heightSamples.Length];
            for (int row = 0; row < rowCount; row++)
            {
                for (int column = 0; column < columnCount; column++)
                    joltSamples[column * rowCount + row] = heightSamples[row * columnCount + column];
            }

            Vector3 offset = Vector3.Zero;
            Vector3 scale = new(rowScale, heightScale, columnScale);
            using HeightFieldShapeSettings settings = new(joltSamples.AsSpan(), offset, scale, (uint)rowCount)
            {
                // Jolt accepts 1-8 bits per block-relative sample; 8 is its highest-fidelity mode.
                BitsPerSample = 8,
            };
            return new HeightFieldShape(in settings);
        }

        return CreateHeightFieldMesh(
            heightSamples,
            rowCount,
            columnCount,
            heightScale,
            rowScale,
            columnScale,
            cells);
    }

    private static Shape CreateHeightFieldMesh(
        ReadOnlySpan<float> heightSamples,
        int rowCount,
        int columnCount,
        float heightScale,
        float rowScale,
        float columnScale,
        ReadOnlySpan<JoltHeightFieldCell> cells)
    {
        Vector3[] vertices = new Vector3[heightSamples.Length];
        for (int row = 0; row < rowCount; row++)
        {
            for (int column = 0; column < columnCount; column++)
            {
                int vertexIndex = row * columnCount + column;
                vertices[vertexIndex] = new Vector3(
                    row * rowScale,
                    heightSamples[vertexIndex] * heightScale,
                    column * columnScale);
            }
        }

        int maximumTriangleCount = checked((rowCount - 1) * (columnCount - 1) * 2);
        uint[] indices = new uint[maximumTriangleCount * 3];
        uint[] faceIndices = new uint[maximumTriangleCount];
        int writtenTriangleCount = 0;

        for (int row = 0; row < rowCount - 1; row++)
        {
            for (int column = 0; column < columnCount - 1; column++)
            {
                int cellIndex = row * (columnCount - 1) + column;
                JoltHeightFieldCell cell = cells[cellIndex];
                uint topLeft = (uint)(row * columnCount + column);
                uint topRight = (uint)((row + 1) * columnCount + column);
                uint bottomLeft = (uint)(row * columnCount + column + 1);
                uint bottomRight = (uint)((row + 1) * columnCount + column + 1);

                if (cell.TessellatedDiagonal)
                {
                    if (!cell.LowerTriangleHole)
                        AddHeightFieldTriangle(indices, faceIndices, ref writtenTriangleCount, topLeft, bottomLeft, bottomRight, (uint)(cellIndex * 2));
                    if (!cell.UpperTriangleHole)
                        AddHeightFieldTriangle(indices, faceIndices, ref writtenTriangleCount, topLeft, bottomRight, topRight, (uint)(cellIndex * 2 + 1));
                }
                else
                {
                    if (!cell.LowerTriangleHole)
                        AddHeightFieldTriangle(indices, faceIndices, ref writtenTriangleCount, topRight, bottomLeft, bottomRight, (uint)(cellIndex * 2));
                    if (!cell.UpperTriangleHole)
                        AddHeightFieldTriangle(indices, faceIndices, ref writtenTriangleCount, topLeft, bottomLeft, topRight, (uint)(cellIndex * 2 + 1));
                }
            }
        }

        if (writtenTriangleCount == 0)
        {
            Vector3 center = new(
                (rowCount - 1) * rowScale * 0.5f,
                0.0f,
                (columnCount - 1) * columnScale * 0.5f);
            using EmptyShapeSettings settings = new(center);
            return settings.Create();
        }

        return CreateTriangleMesh(
            vertices,
            indices.AsSpan(0, writtenTriangleCount * 3),
            faceIndices.AsSpan(0, writtenTriangleCount),
            Vector3.One,
            Quaternion.Identity);
    }

    private static void AddHeightFieldTriangle(
        uint[] indices,
        uint[] faceIndices,
        ref int triangleCount,
        uint i0,
        uint i1,
        uint i2,
        uint faceIndex)
    {
        int indexOffset = triangleCount * 3;
        indices[indexOffset] = i0;
        indices[indexOffset + 1] = i1;
        indices[indexOffset + 2] = i2;
        faceIndices[triangleCount] = faceIndex;
        triangleCount++;
    }

    private static bool CanUseNativeHeightField(
        int rowCount,
        int columnCount,
        ReadOnlySpan<JoltHeightFieldCell> cells)
    {
        // Jolt's native height field is square, requires at least two 2x2 blocks, uses
        // one fixed diagonal per cell, and cannot represent PhysX per-triangle holes.
        if (rowCount != columnCount || rowCount < 4)
            return false;

        for (int index = 0; index < cells.Length; index++)
        {
            JoltHeightFieldCell cell = cells[index];
            if (!cell.TessellatedDiagonal || cell.LowerTriangleHole || cell.UpperTriangleHole)
                return false;
        }

        return true;
    }

    private static Vector3[] TransformPhysxMeshVertices(
        ReadOnlySpan<Vector3> vertices,
        Vector3 scale,
        Quaternion scaleRotation,
        bool allowNegativeScale)
    {
        ValidateMeshScale(scale, allowNegativeScale);
        Quaternion rotation = NormalizeRotation(scaleRotation, nameof(scaleRotation));
        Quaternion inverseRotation = Quaternion.Conjugate(rotation);
        Vector3[] transformed = new Vector3[vertices.Length];

        for (int index = 0; index < vertices.Length; index++)
        {
            Vector3 vertex = vertices[index];
            if (!IsFinite(vertex))
                throw new ArgumentException($"Mesh vertex {index} is not finite.", nameof(vertices));

            // PxMeshScale::transform(v) = rotation^-1 * (scale * (rotation * v)).
            Vector3 scaleAxesVertex = Vector3.Transform(vertex, rotation) * scale;
            transformed[index] = Vector3.Transform(scaleAxesVertex, inverseRotation);
        }

        return transformed;
    }

    private static JoltShapeMetadata ApplyLocalPose(
        JoltShapeMetadata child,
        Vector3 localPosition,
        Quaternion localRotation)
    {
        Vector3 position = ValidatePosition(localPosition, nameof(localPosition));
        Quaternion rotation = NormalizeRotation(localRotation, nameof(localRotation));
        if (position == Vector3.Zero && IsIdentity(rotation))
            return child;

        try
        {
            Shape shape = new RotatedTranslatedShape(position, rotation, child.Shape);
            return new JoltDecoratedShapeMetadata(shape, child, position, rotation);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private static void ValidateMeshScale(Vector3 scale, bool allowNegativeScale)
    {
        if (!IsFinite(scale))
            throw new ArgumentOutOfRangeException(nameof(scale), "Mesh scale must be finite.");

        Vector3 absolute = Vector3.Abs(scale);
        if (absolute.X < MinimumMeshScale || absolute.Y < MinimumMeshScale || absolute.Z < MinimumMeshScale
            || absolute.X > MaximumMeshScale || absolute.Y > MaximumMeshScale || absolute.Z > MaximumMeshScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(scale),
                $"Mesh scale magnitudes must be in [{MinimumMeshScale}, {MaximumMeshScale}].");
        }

        if (!allowNegativeScale && (scale.X < 0.0f || scale.Y < 0.0f || scale.Z < 0.0f))
            throw new ArgumentOutOfRangeException(nameof(scale), "PhysX convex mesh scale components must be positive.");
    }

    private static void ValidateHeightScale(float scale, string parameterName)
    {
        if (!float.IsFinite(scale) || scale < MinimumMeshScale || scale > MaximumMeshScale)
            throw new ArgumentOutOfRangeException(parameterName, $"Height-field scale must be in [{MinimumMeshScale}, {MaximumMeshScale}].");
    }

    private static Vector3 ValidatePosition(Vector3 position, string parameterName)
        => IsFinite(position)
            ? position
            : throw new ArgumentOutOfRangeException(parameterName, "Collider position must be finite.");

    internal static Quaternion NormalizeRotation(Quaternion rotation, string parameterName)
    {
        if (!IsFinite(rotation))
            throw new ArgumentOutOfRangeException(parameterName, "Collider rotation must be finite.");

        float lengthSquared = rotation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1.0e-12f)
            throw new ArgumentOutOfRangeException(parameterName, "Collider rotation must be non-zero.");

        return Quaternion.Normalize(rotation);
    }

    private static bool IsIdentity(Quaternion rotation)
        => rotation == Quaternion.Identity || rotation == new Quaternion(0.0f, 0.0f, 0.0f, -1.0f);

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}

internal readonly record struct JoltHeightFieldCell(
    bool TessellatedDiagonal,
    bool LowerTriangleHole,
    bool UpperTriangleHole);

internal readonly record struct JoltTriangleVertices(Vector3 A, Vector3 B, Vector3 C);

internal class JoltShapeMetadata : IDisposable
{
    protected const uint InvalidFaceIndex = uint.MaxValue;

    protected JoltShapeMetadata(Shape shape)
    {
        Shape = shape ?? throw new ArgumentNullException(nameof(shape));
    }

    public Shape Shape { get; }

    public static JoltShapeMetadata Create(Shape shape)
        => shape switch
        {
            MeshShape mesh => new JoltMeshShapeMetadata(mesh),
            HeightFieldShape heightField => new JoltHeightFieldShapeMetadata(heightField),
            TriangleShape triangle => new JoltTriangleShapeMetadata(triangle),
            _ => new JoltShapeMetadata(shape),
        };

    public virtual uint ResolveFaceIndex(SubShapeID subShapeID)
        => InvalidFaceIndex;

    public virtual bool TryResolveBarycentricUV(
        SubShapeID subShapeID,
        Vector3 localPosition,
        out Vector2 uv)
    {
        uv = Vector2.Zero;
        return false;
    }

    public virtual void Dispose()
        => Shape.Dispose();
}

internal sealed class JoltMeshShapeMetadata : JoltShapeMetadata
{
    private readonly MeshShape _shape;
    private readonly IReadOnlyDictionary<uint, JoltTriangleVertices>? _triangles;

    public JoltMeshShapeMetadata(MeshShape shape)
        : this(shape, null)
    {
    }

    public JoltMeshShapeMetadata(
        MeshShape shape,
        IReadOnlyDictionary<uint, JoltTriangleVertices>? triangles)
        : base(shape)
    {
        _shape = shape;
        _triangles = triangles;
    }

    public override uint ResolveFaceIndex(SubShapeID subShapeID)
        => _shape.GetTriangleUserData(subShapeID.Value);

    public override bool TryResolveBarycentricUV(
        SubShapeID subShapeID,
        Vector3 localPosition,
        out Vector2 uv)
    {
        uv = Vector2.Zero;
        uint faceIndex = ResolveFaceIndex(subShapeID);
        if (_triangles is null || !_triangles.TryGetValue(faceIndex, out JoltTriangleVertices triangle))
            return false;

        Vector3 edge0 = triangle.B - triangle.A;
        Vector3 edge1 = triangle.C - triangle.A;
        Vector3 point = localPosition - triangle.A;
        float d00 = Vector3.Dot(edge0, edge0);
        float d01 = Vector3.Dot(edge0, edge1);
        float d11 = Vector3.Dot(edge1, edge1);
        float d20 = Vector3.Dot(point, edge0);
        float d21 = Vector3.Dot(point, edge1);
        float denominator = d00 * d11 - d01 * d01;
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= 1.0e-12f)
            return false;

        // PhysX PxRaycastHit.u/v are barycentric weights for vertices B and C.
        uv = new Vector2(
            (d11 * d20 - d01 * d21) / denominator,
            (d00 * d21 - d01 * d20) / denominator);
        return float.IsFinite(uv.X) && float.IsFinite(uv.Y);
    }
}

internal sealed class JoltHeightFieldShapeMetadata(HeightFieldShape shape) : JoltShapeMetadata(shape)
{
    public override uint ResolveFaceIndex(SubShapeID subShapeID)
        => subShapeID.Value;
}

internal sealed class JoltTriangleShapeMetadata(TriangleShape shape) : JoltShapeMetadata(shape)
{
    public override uint ResolveFaceIndex(SubShapeID subShapeID)
        => 0;
}

internal sealed class JoltDecoratedShapeMetadata(
    Shape shape,
    JoltShapeMetadata child,
    Vector3 localPosition,
    Quaternion localRotation) : JoltShapeMetadata(shape)
{
    public override uint ResolveFaceIndex(SubShapeID subShapeID)
        => child.ResolveFaceIndex(subShapeID);

    public override bool TryResolveBarycentricUV(
        SubShapeID subShapeID,
        Vector3 position,
        out Vector2 uv)
    {
        Vector3 childPosition = Vector3.Transform(
            position - localPosition,
            Quaternion.Conjugate(localRotation));
        return child.TryResolveBarycentricUV(subShapeID, childPosition, out uv);
    }

    public override void Dispose()
    {
        base.Dispose();
        child.Dispose();
    }
}

internal sealed class JoltCompoundShapeMetadata : JoltShapeMetadata
{
    private readonly JoltShapeMetadata[] _children;
    private readonly Vector3[] _childPositions;
    private readonly Quaternion[] _childRotations;
    private readonly int _childIndexBits;
    private readonly uint _childIndexMask;

    public JoltCompoundShapeMetadata(
        Shape shape,
        JoltShapeMetadata[] children,
        Vector3[] childPositions,
        Quaternion[] childRotations)
        : base(shape)
    {
        if (children.Length < 2)
            throw new ArgumentException("Compound metadata requires at least two children.", nameof(children));
        if (childPositions.Length != children.Length || childRotations.Length != children.Length)
            throw new ArgumentException("Compound child transforms must match the child count.", nameof(childPositions));

        _children = children;
        _childPositions = childPositions;
        _childRotations = childRotations;
        _childIndexBits = 32 - BitOperations.LeadingZeroCount((uint)(children.Length - 1));
        _childIndexMask = (1u << _childIndexBits) - 1u;
    }

    public override uint ResolveFaceIndex(SubShapeID subShapeID)
    {
        uint childIndex = subShapeID.Value & _childIndexMask;
        if (childIndex >= _children.Length)
            return InvalidFaceIndex;

        uint fillBits = uint.MaxValue << (32 - _childIndexBits);
        SubShapeID remainder = new((subShapeID.Value >> _childIndexBits) | fillBits);
        return _children[childIndex].ResolveFaceIndex(remainder);
    }

    public override bool TryResolveBarycentricUV(
        SubShapeID subShapeID,
        Vector3 localPosition,
        out Vector2 uv)
    {
        uint childIndex = subShapeID.Value & _childIndexMask;
        if (childIndex >= _children.Length)
        {
            uv = Vector2.Zero;
            return false;
        }

        uint fillBits = uint.MaxValue << (32 - _childIndexBits);
        SubShapeID remainder = new((subShapeID.Value >> _childIndexBits) | fillBits);
        Vector3 childPosition = Vector3.Transform(
            localPosition - _childPositions[childIndex],
            Quaternion.Conjugate(_childRotations[childIndex]));
        return _children[childIndex].TryResolveBarycentricUV(remainder, childPosition, out uv);
    }

    public override void Dispose()
    {
        base.Dispose();
        for (int index = 0; index < _children.Length; index++)
            _children[index].Dispose();
    }
}
